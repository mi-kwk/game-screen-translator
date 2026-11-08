using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
namespace GameScreenTranslator;

/// <summary>翻訳履歴の 1 件。</summary>
public record HistoryEntry(long Id, DateTime Created, string Game, string Source, string English, string Japanese);

/// <summary>検索結果(履歴、意味の近さ、言葉がそのまま含まれるか、いちばん近かった部分)。</summary>
public record HistoryHit(HistoryEntry Entry, double Score, bool KeywordMatch, string BestPart);

/// <summary>
/// 翻訳履歴を PC の中のデータベース(SQLite、LocalAppData/GameScreenTranslator/history.db)に保存する。外には送らない。
/// 訳を段落ごとの小さなまとまりに分け、まとまりごとに埋め込み用モデルで「意味の数値」(埋め込み)を作って保存する。
/// 長い文書を 1 つの数値にすると意味がぼやけるため、検索では「いちばん近いまとまり」の近さで履歴を並べる。
/// </summary>
public sealed partial class HistoryStore
{
    public static readonly string DefaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameScreenTranslator", "history.db");
    private readonly string connectionString;

    public HistoryStore(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString();
        using var db = Open();
        Execute(db, """
            CREATE TABLE IF NOT EXISTS entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created TEXT NOT NULL,
                game TEXT NOT NULL,
                source TEXT NOT NULL,
                english TEXT NOT NULL,
                japanese TEXT NOT NULL,
                english_key TEXT NOT NULL,
                embed_model TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS entries_key ON entries(game, english_key);
            CREATE INDEX IF NOT EXISTS entries_created ON entries(created);
            CREATE TABLE IF NOT EXISTS chunks (
                entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
                idx INTEGER NOT NULL,
                text TEXT NOT NULL,
                embedding BLOB NOT NULL,
                PRIMARY KEY (entry_id, idx)
            );
            """);
    }

    /// <summary>
    /// 履歴を保存して id を返す。同じゲームで同じ英文(空白や大文字小文字の違いは無視)がすでにあれば、
    /// 新しい訳と日時で上書きし、意味の数値は作り直す。
    /// </summary>
    public long Add(string game, string source, string english, string japanese, DateTime? created = null)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO entries (created, game, source, english, japanese, english_key) VALUES ($created, $game, $source, $english, $japanese, $key)
            ON CONFLICT(game, english_key) DO UPDATE SET created = excluded.created, source = excluded.source, english = excluded.english,
                japanese = excluded.japanese, embed_model = NULL
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$created", (created ?? DateTime.Now).ToString("O"));
        command.Parameters.AddWithValue("$game", game);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$english", english.Trim());
        command.Parameters.AddWithValue("$japanese", japanese.Trim());
        command.Parameters.AddWithValue("$key", Key(english));
        var id = (long)command.ExecuteScalar()!;
        using var clear = db.CreateCommand();
        clear.CommandText = "DELETE FROM chunks WHERE entry_id = $id";
        clear.Parameters.AddWithValue("$id", id);
        clear.ExecuteNonQuery();
        transaction.Commit();
        return id;
    }

    /// <summary>まとまりごとの意味の数値を保存する(texts と vectors は同じ順)。</summary>
    public void SetEmbeddings(long id, IReadOnlyList<string> texts, IReadOnlyList<float[]> vectors, string model)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using (var clear = db.CreateCommand())
        {
            clear.CommandText = "DELETE FROM chunks WHERE entry_id = $id";
            clear.Parameters.AddWithValue("$id", id);
            clear.ExecuteNonQuery();
        }
        for (var i = 0; i < texts.Count; i++)
        {
            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO chunks (entry_id, idx, text, embedding) VALUES ($id, $idx, $text, $v)";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$idx", i);
            insert.Parameters.AddWithValue("$text", texts[i]);
            insert.Parameters.AddWithValue("$v", ToBytes(vectors[i]));
            insert.ExecuteNonQuery();
        }
        using var update = db.CreateCommand();
        update.CommandText = "UPDATE entries SET embed_model = $m WHERE id = $id";
        update.Parameters.AddWithValue("$m", model);
        update.Parameters.AddWithValue("$id", id);
        update.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>意味の数値がまだ無い(または別のモデルで作った)履歴。</summary>
    public List<HistoryEntry> PendingEmbeddings(string model, int limit = 50)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM entries WHERE embed_model IS NULL OR embed_model IS NOT $m ORDER BY created DESC LIMIT $limit";
        command.Parameters.AddWithValue("$m", model);
        command.Parameters.AddWithValue("$limit", limit);
        return ReadEntries(command);
    }

    /// <summary>新しい順の一覧。game が空でなければ、そのゲームだけ。</summary>
    public List<HistoryEntry> Recent(string? game = null, int limit = 200)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM entries WHERE ($game IS NULL OR game = $game) ORDER BY created DESC LIMIT $limit";
        command.Parameters.AddWithValue("$game", (object?)NullIfEmpty(game) ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        return ReadEntries(command);
    }

    /// <summary>
    /// 意味で探す + 言葉そのままで探す。
    /// 言葉が英文か訳にそのまま含まれる履歴を先に並べ、それぞれの中は意味の近さ(いちばん近いまとまりのコサイン類似度)の高い順。
    /// 意味の近さが minSimilarity 未満で、言葉も含まれない履歴は出さない。
    /// queryVector が null(埋め込み用モデルが使えない)なら、言葉そのままで探すだけにする。
    /// </summary>
    public List<HistoryHit> Search(string query, float[]? queryVector, string? game = null, int limit = 50, double minSimilarity = DefaultMinSimilarity)
    {
        query = query.Trim();
        if (query.Length == 0) return Recent(game, limit).Select(e => new HistoryHit(e, 0, false, "")).ToList();
        using var db = Open();
        var best = new Dictionary<long, (double Similarity, string Text)>();
        if (queryVector is not null)
        {
            using var chunks = db.CreateCommand();
            chunks.CommandText = "SELECT c.entry_id, c.text, c.embedding FROM chunks c JOIN entries e ON e.id = c.entry_id WHERE ($game IS NULL OR e.game = $game)";
            chunks.Parameters.AddWithValue("$game", (object?)NullIfEmpty(game) ?? DBNull.Value);
            using var reader = chunks.ExecuteReader();
            while (reader.Read())
            {
                var similarity = Cosine(queryVector, FromBytes((byte[])reader[2]));
                var id = reader.GetInt64(0);
                if (!best.TryGetValue(id, out var current) || similarity > current.Similarity) best[id] = (similarity, reader.GetString(1));
            }
        }
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM entries WHERE ($game IS NULL OR game = $game)";
        command.Parameters.AddWithValue("$game", (object?)NullIfEmpty(game) ?? DBNull.Value);
        var hits = new List<HistoryHit>();
        foreach (var entry in ReadEntries(command))
        {
            var keyword = entry.English.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Japanese.Contains(query, StringComparison.OrdinalIgnoreCase);
            var (similarity, part) = best.TryGetValue(entry.Id, out var b) ? b : (0, "");
            if (!keyword && similarity < minSimilarity) continue;
            if (keyword && (part.Length == 0 || !part.Contains(query, StringComparison.OrdinalIgnoreCase)))
                part = Chunk(entry.Japanese).FirstOrDefault(c => c.Contains(query, StringComparison.OrdinalIgnoreCase)) ?? part;
            hits.Add(new HistoryHit(entry, similarity, keyword, part));
        }
        // 言葉がそのまま含まれる履歴(固有名詞など、確実に探したいもの)を先に、その中と残りは意味の近い順
        return hits.OrderByDescending(h => h.KeywordMatch).ThenByDescending(h => h.Score).ThenByDescending(h => h.Entry.Created).Take(limit).ToList();
    }

    /// <summary>意味で探すときに出す、近さの下限(bge-m3 で実際の訳を使って決めた値)。</summary>
    public const double DefaultMinSimilarity = 0.5;

    public List<string> Games()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT DISTINCT game FROM entries WHERE game <> '' ORDER BY game";
        using var reader = command.ExecuteReader();
        var games = new List<string>();
        while (reader.Read()) games.Add(reader.GetString(0));
        return games;
    }

    public int Count()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM entries";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Delete(long id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM entries WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Clear()
    {
        using var db = Open();
        Execute(db, "DELETE FROM chunks; DELETE FROM entries; VACUUM;");
    }

    /// <summary>
    /// 訳を、意味の数値を作る小さなまとまりに分ける。
    /// 空行と区切り線(---)で段落に分け、見出しは次の段落とつなぎ、短い段落は 300 文字ほどまでまとめる。
    /// 長すぎる段落は文(。)の区切りで分ける。Markdown の記号は外す。
    /// </summary>
    public static List<string> Chunk(string japanese)
    {
        var paragraphs = new List<string>();
        foreach (var block in Regex.Split(japanese.Replace("\r\n", "\n"), @"\n\s*\n|\n\s*-{3,}\s*\n"))
        {
            var text = MarkdownView.ToPlainText(block).Trim();
            if (text.Length == 0 || text.Trim('-', ' ').Length == 0) continue;
            if (text.Length <= MaxPart) { paragraphs.Add(text); continue; }
            var sentence = new StringBuilder();
            foreach (var part in SentenceEnd().Split(text))
            {
                if (sentence.Length + part.Length > TargetPart && sentence.Length > 0) { paragraphs.Add(sentence.ToString().Trim()); sentence.Clear(); }
                sentence.Append(part);
            }
            if (sentence.Length > 0) paragraphs.Add(sentence.ToString().Trim());
        }
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var p in paragraphs)
        {
            var isHeading = p.Length <= 40 && !p.EndsWith('。') && !p.EndsWith('？') && !p.EndsWith('!') && !p.EndsWith('！');
            // 見出しは新しいまとまりの先頭にする。普通の段落は 300 文字を超えるまで前のまとまりにつなぐ(見出しの直後では分けない)
            var split = isHeading ? !EndsWithHeading(current) : current.Length + p.Length > TargetPart && !EndsWithHeading(current);
            if (current.Length > 0 && split) { chunks.Add(current.ToString().Trim()); current.Clear(); }
            if (current.Length > 0) current.Append('\n');
            current.Append(p);
            if (isHeading) current.Append(HeadingMark);
        }
        if (current.Length > 0) chunks.Add(current.ToString().Trim());
        return chunks.Select(c => c.Replace(HeadingMark, "")).Where(c => c.Length > 0).ToList();
    }

    /// <summary>まとまりの目安の長さ。長いと複数の話題が混ざって意味がぼやけるため、Q&A の 1 問ほどにする(実際の訳で調整)。</summary>
    public const int TargetPart = 150, MaxPart = 250;
    private const string HeadingMark = "\u0001";
    private static bool EndsWithHeading(StringBuilder sb) => sb.Length > 0 && sb[^1] == HeadingMark[0];

    /// <summary>2 つの数値の並び(埋め込み)の向きの近さ。1 に近いほど意味が近い。</summary>
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    private const string Columns = "id, created, game, source, english, japanese";

    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }

    private static void Execute(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }

    private static List<HistoryEntry> ReadEntries(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var list = new List<HistoryEntry>();
        while (reader.Read())
            list.Add(new HistoryEntry(reader.GetInt64(0), DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return list;
    }

    private static string Key(string english) => string.Join(' ', english.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static byte[] ToBytes(float[] v) { var b = new byte[v.Length * 4]; Buffer.BlockCopy(v, 0, b, 0, b.Length); return b; }
    private static float[] FromBytes(byte[] b) { var v = new float[b.Length / 4]; Buffer.BlockCopy(b, 0, v, 0, b.Length); return v; }

    [GeneratedRegex(@"(?<=[。！？!?])")]
    private static partial Regex SentenceEnd();
}

/// <summary>Ollama の埋め込み用モデル(初期値 bge-m3)で、文章を「意味の数値」に変える。</summary>
public sealed class EmbeddingService(HttpClient client)
{
    public async Task<float[]> EmbedAsync(AppSettings settings, string text, CancellationToken cancellationToken = default) =>
        (await EmbedManyAsync(settings, [text], cancellationToken))[0];

    /// <summary>いくつかの文章をまとめて意味の数値に変える。モデルが無いときは、取り込み方を案内する例外を出す。</summary>
    public async Task<List<float[]>> EmbedManyAsync(AppSettings settings, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var model = settings.EmbedModel.Trim();
        var input = new JsonArray(texts.Select(t => (JsonNode)JsonValue.Create(t.Length > 2000 ? t[..2000] : t)!).ToArray());
        var body = new JsonObject { ["model"] = model, ["input"] = input };
        HttpResponseMessage response;
        try { response = await client.PostAsJsonAsync(settings.OllamaUrl.Trim().TrimEnd('/') + "/api/embed", body, cancellationToken); }
        catch (HttpRequestException) { throw new InvalidOperationException("Ollama に接続できないため、意味で探せません(言葉そのままで探します)。"); }
        using (response)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException($"埋め込み用のモデル「{model}」が見つかりません。コマンドプロンプトで「ollama pull {model}」を実行すると、意味で探せるようになります。");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"意味の数値を作れませんでした(HTTP {(int)response.StatusCode})。");
            var vectors = JsonNode.Parse(content)?["embeddings"]?.AsArray().Select(v => v!.AsArray().Select(n => n!.GetValue<float>()).ToArray()).ToList();
            if (vectors is null || vectors.Count != texts.Count) throw new InvalidOperationException("意味の数値を作れませんでした。");
            return vectors;
        }
    }
}

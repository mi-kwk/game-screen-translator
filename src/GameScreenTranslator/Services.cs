using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
namespace GameScreenTranslator;
public static partial class OcrService
{
    /// <summary>画像の英語を読み取り、画面上のまとまりごとに並べ直した英文を返す。</summary>
    public static async Task<string> ReadAsync(BitmapSource image) => (await ReadLayoutAsync(image)).Text;

    public static async Task<OcrArrangement> ReadLayoutAsync(BitmapSource image)
    {
        var lines = await ReadLinesAsync(image);
        var arranged = OcrLayout.Arrange(lines);
        return arranged with { Text = Cleanup(arranged.Text) };
    }

    public static async Task<IReadOnlyList<OcrLineBox>> ReadLinesAsync(BitmapSource image)
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
            ?? throw new InvalidOperationException("英語のOCRが利用できません。Windowsの設定で英語の言語パックを追加してください。");
        var limit = (double)OcrEngine.MaxImageDimension;
        var scale = Math.Min(1, limit / Math.Max(image.PixelWidth, image.PixelHeight));
        if (scale < 1) image = new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        { writer.WriteBytes(buffer.ToArray()); await writer.StoreAsync(); await writer.FlushAsync(); }
        stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        return result.Lines.Where(line => line.Words.Count > 0).Select(line =>
        {
            var left = line.Words.Min(w => w.BoundingRect.X);
            var top = line.Words.Min(w => w.BoundingRect.Y);
            var right = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
            var bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            return new OcrLineBox(line.Text, left, top, right - left, bottom - top);
        }).ToList();
    }

    /// <summary>OCR がよく間違える、数字に挟まれた英字の O を 0 に直す(例: 0.4.O.192 → 0.4.0.192)。</summary>
    public static string Cleanup(string text) => DigitO().Replace(text, "0");

    [GeneratedRegex(@"(?<=\d\.?)O(?=\.?\d)")]
    private static partial Regex DigitO();
}

public sealed partial class TranslationService(HttpClient client)
{
    private const string BasePrompt = """
        あなたはゲームの英語テキストを日本語に翻訳する翻訳者です。
        - 自然で読みやすい日本語にする。ゲームのニュースやパッチノートの文体に合わせる。
        - 固有名詞(ゲーム名、地名、マップ名、組織名、アイテム名、人名)は原文の英語のまま残す。
        - 見出し・箇条書き・段落の構成は保つ。
        - 画面の UI の断片(ボタン名、日付、数字だけの行、ゲームのロゴ、意味のない文字列)は省く。
        - 原文の意味を足したり減らしたりしない。否定・肯定を取り違えない。
        - 翻訳文だけを出力し、説明や前置きは書かない。
        - 原文の英語の文をそのまま出力に含めない。英文を書き写してから訳す、という書き方はしない。
        - 「---」だけの行は、画面上で欄が分かれている区切り。訳でも同じ位置に「---」の行を残す。

        出力は読みやすい Markdown にする。
        - 記事やセクションの見出しは「## 見出し」、小見出しは「### 小見出し」にする。
        - 箇条書きは「- 」、番号付きの手順は「1. 」にする。
        - Q&A の質問の文は **太字** にする。重要な語句だけを **太字** にしてよいが、使いすぎない。
        - 段落の間は空行を入れる。原文にない表やコードブロックは作らない。
        """;

    public static string BuildSystemPrompt(AppSettings settings)
    {
        var prompt = BasePrompt;
        var game = settings.CurrentGame;
        if (game is not null)
            prompt += $"\n\n対象のゲーム:{game.Name.Trim()}" + (string.IsNullOrWhiteSpace(game.Description) ? "" : $"({game.Description.Trim()})");
        var common = settings.CommonGlossary.Trim();
        var specific = game?.Glossary.Trim() ?? "";
        if (common.Length > 0 || specific.Length > 0)
        {
            prompt += "\n\n次の用語集に必ず従って訳す(左が英語、右が訳語)。同じ語が両方にある場合は、ゲームの用語集を優先する。";
            if (common.Length > 0) prompt += "\n\n[共通の用語集]\n" + common;
            if (specific.Length > 0) prompt += $"\n\n[{game!.Name.Trim()} の用語集]\n" + specific;
        }
        return prompt;
    }

    /// <summary>
    /// Ollama で翻訳する。訳はできたところから <paramref name="onChunk"/> に渡し、最後に訳全体を返す。
    /// </summary>
    public async Task<string> TranslateAsync(string text, AppSettings settings, Action<string>? onChunk = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("翻訳する英語を入力してください。");
        if (string.IsNullOrWhiteSpace(settings.Model)) throw new InvalidOperationException("翻訳に使うモデルを設定してください(例: gemma3:12b)。");
        if (!Uri.TryCreate(settings.OllamaUrl.Trim().TrimEnd('/') + "/api/chat", UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("Ollama のアドレスが正しくありません(例: http://127.0.0.1:11434)。");
        var model = settings.Model.Trim();
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = BuildSystemPrompt(settings) },
                new JsonObject { ["role"] = "user", ["content"] = "次の英文を日本語に翻訳してください。\n\n" + text.Trim() }),
            ["options"] = new JsonObject { ["temperature"] = 0.2, ["num_ctx"] = 8192 },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (HttpRequestException)
        { throw new InvalidOperationException("Ollama に接続できません。Ollama が起動しているか、設定のアドレスを確認してください。"); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(response.StatusCode == HttpStatusCode.NotFound
                    ? $"モデル「{model}」が見つかりません。コマンドプロンプトで「ollama pull {model}」を実行してください。"
                    : $"翻訳に失敗しました(HTTP {(int)response.StatusCode})。{ErrorMessage(error)}");
            }
            // stream: true のとき、Ollama は 1 行に 1 つの JSON を返す。最後の行は "done": true。
            var answer = new StringBuilder();
            try
            {
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken), Encoding.UTF8);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var node = JsonNode.Parse(line);
                    if (node?["error"]?.GetValue<string>() is { } streamError) throw new InvalidOperationException("翻訳に失敗しました。" + streamError);
                    var piece = node?["message"]?["content"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(piece)) { answer.Append(piece); onChunk?.Invoke(piece); }
                    if (node?["done"]?.GetValue<bool>() == true) break;
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            { throw new InvalidOperationException("翻訳の途中で Ollama との接続が切れました。もう一度翻訳してください。"); }
            if (answer.Length == 0 || string.IsNullOrWhiteSpace(answer.ToString())) throw new InvalidOperationException("翻訳結果が空でした。もう一度翻訳してください。");
            return RemoveEchoedEnglish(answer.ToString(), text).Trim();
        }
    }

    /// <summary>
    /// モデルが原文の英文を書き写した段落を取り除く(訳の段落は残す)。
    /// 英字が大半で 8 語以上あり、単語の 6 割以上が原文に出てくる段落を「書き写し」とみなす。
    /// </summary>
    public static string RemoveEchoedEnglish(string answer, string source)
    {
        var sourceWords = new HashSet<string>(WordPattern().Matches(source).Select(m => m.Value.ToLowerInvariant()));
        var paragraphs = Regex.Split(answer.Replace("\r\n", "\n"), @"\n[ \t]*\n");
        var kept = paragraphs.Where(p =>
        {
            var letters = p.Count(char.IsLetter);
            if (letters == 0) return true;
            var latin = p.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
            var words = WordPattern().Matches(p).Select(m => m.Value.ToLowerInvariant()).ToList();
            var echoed = (double)latin / letters >= 0.8 && words.Count >= 8 && words.Count(sourceWords.Contains) >= words.Count * 0.6;
            return !echoed;
        });
        return string.Join("\n\n", kept);
    }

    [GeneratedRegex(@"[A-Za-z]+(?:'[A-Za-z]+)?")]
    private static partial Regex WordPattern();

    private static string ErrorMessage(string content)
    {
        try { return JsonNode.Parse(content)?["error"]?.GetValue<string>() ?? ""; }
        catch (JsonException) { return ""; }
    }
}

/// <summary>ゲームごとの設定。名前と説明は翻訳の指示に入れ、単語辞書はそのゲームの用語集として使う。</summary>
public record GameProfile(string Name, string Description, string Glossary);

public record AppSettings
{
    public string OllamaUrl { get; init; } = "http://127.0.0.1:11434";
    public string Model { get; init; } = "gemma3:12b";
    /// <summary>この PC の Ollama が止まっていたら自動で起動する。</summary>
    public bool AutoStartOllama { get; init; } = true;
    /// <summary>どのゲームでも使う単語辞書。</summary>
    public string CommonGlossary { get; init; } = DefaultCommonGlossary;
    public List<GameProfile> Games { get; init; } = [];
    /// <summary>選んでいるゲームの名前。空なら共通の単語辞書だけを使う。</summary>
    public string SelectedGame { get; init; } = "";
    /// <summary>日本語訳の文字の大きさ。</summary>
    public double FontSize { get; init; } = 17;

    [System.Text.Json.Serialization.JsonIgnore]
    public GameProfile? CurrentGame => Games.FirstOrDefault(g => g.Name == SelectedGame);

    public static AppSettings Default => new();

    public const string DefaultCommonGlossary = """
        patch notes = パッチノート
        update = アップデート
        hotfix = ホットフィックス
        known issues = 既知の不具合
        server maintenance = サーバーメンテナンス
        season = シーズン
        event = イベント
        quest = クエスト
        mission = ミッション
        objective = 目標
        XP / experience = 経験値
        level up = レベルアップ
        buff = バフ
        nerf = ナーフ
        cooldown = クールダウン
        loot = 戦利品
        drop = ドロップ
        gear = 装備
        inventory = インベントリ
        crafting = クラフト
        skin = スキン
        cosmetics = 見た目のアイテム
        matchmaking = マッチメイキング
        lobby = ロビー
        spawn / respawn = スポーン / リスポーン
        friendly fire = フレンドリーファイア
        recoil = リコイル
        aim down sights (ADS) = ADS(サイトをのぞいて構える)
        PvP / PvE = PvP / PvE(訳さない)
        NPC = NPC(訳さない)
        boss = ボス
        stay tuned = 続報をお待ちください
        """;
}

public static class SettingsStore
{
    private static readonly string DefaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameScreenTranslator", "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return AppSettings.Default;
        var json = File.ReadAllText(path);
        var node = JsonNode.Parse(json) as JsonObject;
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? AppSettings.Default;
        var games = (loaded.Games ?? []).Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Name))
            .Select(g => g with { Description = g.Description ?? "", Glossary = g.Glossary ?? "" }).ToList();
        var selected = loaded.SelectedGame ?? "";
        // 以前の形式(ゲーム名と単語辞書が 1 つだけ)からの移行
        if (node is not null && !node.ContainsKey("Games"))
        {
            var oldName = node["GameName"]?.GetValue<string>()?.Trim() ?? "";
            var oldGlossary = node["Glossary"]?.GetValue<string>()?.Trim() ?? "";
            if (oldName.Length > 0 || oldGlossary.Length > 0)
            {
                var name = oldName.Length > 0 ? oldName : "ゲーム 1";
                games.Add(new GameProfile(name, "", oldGlossary));
                selected = name;
            }
        }
        return loaded with
        {
            OllamaUrl = string.IsNullOrWhiteSpace(loaded.OllamaUrl) ? AppSettings.Default.OllamaUrl : loaded.OllamaUrl,
            Model = string.IsNullOrWhiteSpace(loaded.Model) ? AppSettings.Default.Model : loaded.Model,
            CommonGlossary = node is not null && !node.ContainsKey("CommonGlossary") ? AppSettings.DefaultCommonGlossary : loaded.CommonGlossary ?? "",
            Games = games,
            SelectedGame = games.Any(g => g.Name == selected) ? selected : "",
            FontSize = loaded.FontSize is >= 10 and <= 40 ? loaded.FontSize : AppSettings.Default.FontSize,
        };
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
    }
}

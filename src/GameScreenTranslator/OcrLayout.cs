using System.Text;
using System.Text.RegularExpressions;
namespace GameScreenTranslator;

/// <summary>OCR で読み取った 1 行と、その位置(画像のピクセル)。</summary>
public record OcrLineBox(string Text, double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>並べ直した英文と、英語以外と判断して外したまとまりの数。</summary>
public record OcrArrangement(string Text, int SkippedBlocks);

/// <summary>
/// OCR の行を、画面上のまとまり(段組み・横に並んだ欄など)に分けて並べ直す。
/// - まとまりの間には「---」の行を入れる(訳でも区切り線として残す)。
/// - まとまりの中で行の間が空いているところは、段落として空行を入れる。
/// - 英語の OCR で崩れた英語以外の文章(ロシア語など)のまとまりは外す。
/// </summary>
public static partial class OcrLayout
{
    public const string Separator = "---";

    public static OcrArrangement Arrange(IReadOnlyList<OcrLineBox> lines)
    {
        var items = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text) && l.Height > 0).OrderBy(l => l.Y).ThenBy(l => l.X).ToList();
        if (items.Count == 0) return new("", 0);
        var h = Median(items.Select(l => l.Height));
        // 英単語を 1 つも含まない短い行(数字だけの日付、崩れた日本語のボタン名など)は読み取りの雑音として捨てる
        items = items.Where(l => !IsNoise(l.Text)).ToList();
        if (items.Count == 0) return new("", 0);

        // 1. 行をまとまりに分ける: すぐ下にあり、左端がほぼ揃っていて、横の範囲が重なる行は同じまとまり
        var blocks = new List<List<OcrLineBox>>();
        foreach (var line in items)
        {
            List<OcrLineBox>? best = null;
            var bestGap = double.MaxValue;
            foreach (var block in blocks)
            {
                var last = block[^1];
                var gap = line.Y - last.Bottom;
                var left = block.Min(b => b.X);
                var right = block.Max(b => b.Right);
                var overlaps = line.X < right && line.Right > left;
                // 見出しなど大きな文字の下は行間も広いので、大きいほうの文字の高さで判断する
                var size = Math.Max(h, Math.Max(last.Height, line.Height));
                if (gap > -h * 0.5 && gap <= size * 2.5 && Math.Abs(line.X - left) <= h * 4 && overlaps && gap < bestGap)
                { best = block; bestGap = gap; }
            }
            if (best is null) blocks.Add([line]); else best.Add(line);
        }

        // 2. 短い断片(ボタン名・タブなど)のまとまりは、続けて並ぶものをひとまとめにする
        blocks = blocks.OrderBy(b => b[0].Y).ThenBy(b => b[0].X).ToList();
        var merged = new List<(List<OcrLineBox> Lines, bool Fragments)>();
        foreach (var block in blocks)
        {
            var fragment = IsFragment(block);
            if (fragment && merged.Count > 0 && merged[^1].Fragments) merged[^1].Lines.AddRange(block);
            else merged.Add((block, fragment));
        }

        // 3. 英語以外のまとまりを外し、段落と区切り線を付けて文章にする
        var parts = new List<string>();
        var skipped = 0;
        foreach (var (block, fragments) in merged)
        {
            var text = string.Join(" ", block.Select(l => l.Text));
            if (!LooksEnglish(text)) { skipped++; continue; }
            var sb = new StringBuilder();
            for (var i = 0; i < block.Count; i++)
            {
                if (i > 0) sb.Append(!fragments && block[i].Y - block[i - 1].Bottom > h * 0.9 ? "\n\n" : "\n");
                sb.Append(block[i].Text);
            }
            parts.Add(sb.ToString());
        }
        return new(string.Join($"\n\n{Separator}\n\n", parts), skipped);
    }

    private static bool IsNoise(string text)
    {
        var words = Words(text);
        return words.Count < 6 && !words.Any(w => w.Length >= 3 && EnglishWord().IsMatch(w) && Vowel().IsMatch(w));
    }

    /// <summary>1〜2 行の短い行だけのまとまり(ボタン名・タブ・日付など)。</summary>
    private static bool IsFragment(List<OcrLineBox> block) => block.Count <= 2 && block.All(l => Words(l.Text).Count < 6);

    /// <summary>
    /// 英語らしい文章か。英語の OCR は、ロシア語などを「neAb1 rpß3Hb1X」のような崩れた文字列として読むので、
    /// 英語らしい単語(英字だけ・母音を含む・途中で小文字から大文字に変わらない)の割合と、the / and などの多さで判断する。
    /// 短い文章(5 語以下)は判断できないので英語として扱う。
    /// </summary>
    public static bool LooksEnglish(string text)
    {
        var words = Words(text);
        if (words.Count < 6) return true;
        var english = words.Count(w => EnglishWord().IsMatch(w) && Vowel().IsMatch(w) && !CaseSwitch().IsMatch(w));
        var common = words.Count(w => CommonWords.Contains(w.ToLowerInvariant()));
        var ratio = (double)english / words.Count;
        var commonRatio = (double)common / words.Count;
        return ratio >= 0.6 && (commonRatio >= 0.06 || ratio >= 0.85 || words.Count < 12);
    }

    private static List<string> Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('"', '\'', '“', '”', '‘', '’', '(', ')', '[', ']', '.', ',', ':', ';', '!', '?', '—', '–', '-', '•', '·', '*'))
            .Where(w => w.Length > 0 && !Number().IsMatch(w))
            .ToList();

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private static readonly HashSet<string> CommonWords =
    [
        "the", "a", "an", "and", "or", "but", "of", "to", "in", "on", "at", "by", "for", "with", "from", "into", "as",
        "is", "are", "was", "were", "be", "been", "it", "its", "this", "that", "these", "those", "you", "your", "we", "our",
        "they", "their", "will", "can", "not", "no", "now", "all", "more", "has", "have", "had", "what", "which", "who", "how",
    ];

    [GeneratedRegex(@"^[A-Za-z]+(['’-][A-Za-z]+)*$")]
    private static partial Regex EnglishWord();
    [GeneratedRegex(@"[aeiouyAEIOUY]")]
    private static partial Regex Vowel();
    [GeneratedRegex(@"[a-z][A-Z]")]
    private static partial Regex CaseSwitch();
    [GeneratedRegex(@"^[\d.,:/%+#x×-]+$")]
    private static partial Regex Number();
}

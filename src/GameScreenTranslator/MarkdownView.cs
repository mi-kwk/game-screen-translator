using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
namespace GameScreenTranslator;

/// <summary>
/// 翻訳結果の Markdown を、読みやすい FlowDocument に変える。
/// 見出し・箇条書き・番号付きリスト・引用・区切り線・太字・斜体・コードに対応する(表やリンクは文字のまま)。
/// </summary>
public static partial class MarkdownView
{
    private static readonly Brush Text = Brush("#1F2937");
    private static readonly Brush Heading = Brush("#1E3A8A");
    private static readonly Brush Strong = Brush("#9A3412");
    private static readonly Brush Question = Brush("#1E40AF");
    private static readonly Brush Bullet = Brush("#3B82F6");
    private static readonly Brush QuoteText = Brush("#4B5563");
    private static readonly Brush QuoteBar = Brush("#93C5FD");
    private static readonly Brush CodeBack = Brush("#EEF2F7");
    private static readonly Brush Rule = Brush("#CBD5E1");

    public static FlowDocument ToDocument(string markdown, double fontSize)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI"),
            FontSize = fontSize,
            Foreground = Text,
            PagePadding = new Thickness(12),
            LineHeight = fontSize * 1.6,
            TextAlignment = TextAlignment.Left,
        };
        // 箇条書きの入れ子を追うためのスタック(深さ, List)
        var lists = new List<(int Indent, List List)>();
        Paragraph? paragraph = null;

        void EndParagraph() => paragraph = null;
        void EndLists() { lists.Clear(); }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) { EndParagraph(); EndLists(); continue; }

            if (HeadingLine().Match(line) is { Success: true } h)
            {
                EndParagraph(); EndLists();
                var level = h.Groups[1].Value.Length;
                var p = new Paragraph { FontWeight = FontWeights.Bold, Foreground = Heading, Margin = new Thickness(0, level <= 2 ? 14 : 10, 0, 4) };
                p.FontSize = fontSize * level switch { 1 => 1.6, 2 => 1.4, 3 => 1.2, _ => 1.08 };
                p.LineHeight = p.FontSize * 1.35;
                AddInlines(p.Inlines, h.Groups[2].Value, inHeading: true);
                if (level <= 2) { p.BorderBrush = Rule; p.BorderThickness = new Thickness(0, 0, 0, 1); p.Padding = new Thickness(0, 0, 0, 4); }
                document.Blocks.Add(p);
                continue;
            }
            if (RuleLine().IsMatch(line))
            {
                EndParagraph(); EndLists();
                document.Blocks.Add(new Paragraph { BorderBrush = Rule, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 8, 0, 8), FontSize = 2 });
                continue;
            }
            if (ListLine().Match(line) is { Success: true } li)
            {
                EndParagraph();
                var indent = li.Groups[1].Value.Replace("\t", "    ").Length;
                var ordered = char.IsDigit(li.Groups[2].Value[0]);
                while (lists.Count > 0 && lists[^1].Indent > indent) lists.RemoveAt(lists.Count - 1);
                if (lists.Count == 0 || lists[^1].Indent < indent || IsOrdered(lists[^1].List) != ordered)
                {
                    if (lists.Count > 0 && lists[^1].Indent == indent) lists.RemoveAt(lists.Count - 1);
                    var list = new List
                    {
                        MarkerStyle = ordered ? TextMarkerStyle.Decimal : (lists.Count % 2 == 0 ? TextMarkerStyle.Disc : TextMarkerStyle.Circle),
                        Foreground = Bullet, Margin = new Thickness(0, 2, 0, 6), Padding = new Thickness(22, 0, 0, 0),
                    };
                    if (ordered && int.TryParse(li.Groups[2].Value.TrimEnd('.', ')'), out var start)) list.StartIndex = Math.Max(1, start);
                    if (lists.Count > 0 && lists[^1].List.ListItems.LastListItem is { } parent) parent.Blocks.Add(list);
                    else document.Blocks.Add(list);
                    lists.Add((indent, list));
                }
                var item = new Paragraph { Foreground = Text, Margin = new Thickness(0, 1, 0, 1) };
                AddInlines(item.Inlines, li.Groups[3].Value);
                lists[^1].List.ListItems.Add(new ListItem(item));
                continue;
            }
            if (QuoteLine().Match(line) is { Success: true } q)
            {
                EndLists();
                if (paragraph is null || paragraph.Tag as string != "quote")
                {
                    paragraph = new Paragraph { Tag = "quote", Foreground = QuoteText, BorderBrush = QuoteBar, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2), Margin = new Thickness(0, 4, 0, 8) };
                    document.Blocks.Add(paragraph);
                }
                else paragraph.Inlines.Add(new LineBreak());
                AddInlines(paragraph.Inlines, q.Groups[1].Value);
                continue;
            }
            // 箇条書きの続きの行(字下げされた普通の文)は、直前の項目に足す
            if (lists.Count > 0 && raw.StartsWith("  ") && lists[^1].List.ListItems.LastListItem?.Blocks.LastBlock is Paragraph continued)
            {
                continued.Inlines.Add(new LineBreak());
                AddInlines(continued.Inlines, line.Trim());
                continue;
            }
            EndLists();
            if (paragraph is null || paragraph.Tag is not null)
            {
                paragraph = new Paragraph { Margin = new Thickness(0, 2, 0, 8) };
                document.Blocks.Add(paragraph);
            }
            else paragraph.Inlines.Add(new LineBreak());
            var trimmed = line.Trim();
            // Q&A の質問(「？」で終わる行)は、モデルが太字にしなくても目立たせる
            if (trimmed.EndsWith('？') || trimmed.EndsWith('?'))
            {
                var question = new Span { FontWeight = FontWeights.SemiBold, Foreground = Question };
                AddInlines(question.Inlines, trimmed);
                paragraph.Inlines.Add(question);
            }
            else AddInlines(paragraph.Inlines, trimmed);
        }
        return document;
    }

    /// <summary>コピー用に、Markdown の記号を外した読みやすい文章にする。</summary>
    public static string ToPlainText(string markdown)
    {
        var result = new StringBuilder();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (HeadingLine().Match(line) is { Success: true } h) line = h.Groups[2].Value;
            else if (RuleLine().IsMatch(line)) line = "";
            else if (ListLine().Match(line) is { Success: true } li)
            {
                var indent = new string(' ', li.Groups[1].Value.Replace("\t", "    ").Length / 2 * 2);
                line = indent + (char.IsDigit(li.Groups[2].Value[0]) ? li.Groups[2].Value.TrimEnd(')').TrimEnd('.') + ". " : "・") + li.Groups[3].Value;
            }
            else if (QuoteLine().Match(line) is { Success: true } q) line = q.Groups[1].Value;
            result.AppendLine(InlineMarks().Replace(line, m => m.Groups["b"].Success ? m.Groups["b"].Value : m.Groups["i"].Success ? m.Groups["i"].Value : m.Groups["i2"].Success ? m.Groups["i2"].Value : m.Groups["c"].Value));
        }
        return result.ToString().Trim();
    }

    private static void AddInlines(InlineCollection inlines, string text, bool inHeading = false)
    {
        var last = 0;
        foreach (Match m in InlineMarks().Matches(text))
        {
            if (m.Index > last) inlines.Add(new Run(text[last..m.Index]));
            if (m.Groups["b"].Success)
                inlines.Add(new Bold(new Run(m.Groups["b"].Value)) { Foreground = inHeading ? Heading : Strong });
            else if (m.Groups["i"].Success || m.Groups["i2"].Success)
                inlines.Add(new Italic(new Run(m.Groups["i"].Success ? m.Groups["i"].Value : m.Groups["i2"].Value)));
            else
                inlines.Add(new Run(m.Groups["c"].Value) { Background = CodeBack, FontFamily = new FontFamily("Consolas, MS Gothic") });
            last = m.Index + m.Length;
        }
        if (last < text.Length) inlines.Add(new Run(text[last..]));
    }

    private static bool IsOrdered(List list) => list.MarkerStyle == TextMarkerStyle.Decimal;
    private static SolidColorBrush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$")]
    private static partial Regex HeadingLine();
    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex RuleLine();
    [GeneratedRegex(@"^(\s*)([-*+•・]|\d{1,3}[.)])\s+(.*)$")]
    private static partial Regex ListLine();
    [GeneratedRegex(@"^\s{0,3}>\s?(.*)$")]
    private static partial Regex QuoteLine();
    [GeneratedRegex(@"\*\*(?<b>.+?)\*\*|__(?<b>.+?)__|(?<![\w*])\*(?<i>[^*\s][^*]*?)\*(?![\w*])|(?<![\w_])_(?<i2>[^_\s][^_]*?)_(?![\w_])|`(?<c>[^`]+)`")]
    private static partial Regex InlineMarks();
}

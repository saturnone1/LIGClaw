using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal static partial class MarkdownDocumentRenderer
{
    public static FlowDocument Render(
        string markdown,
        FontFamily fontFamily,
        Brush foreground,
        Brush secondary,
        Brush border,
        Brush mutedSurface,
        FontFamily monoFontFamily)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(1),
            FontFamily = fontFamily,
            FontSize = 14,
            Foreground = foreground,
            LineHeight = 21,
        };
        var lines = Normalize(markdown).Split('\n');
        for (var index = 0; index < lines.Length;)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                index = AddCodeBlock(document, lines, index, monoFontFamily, mutedSurface);
                continue;
            }
            if (index + 1 < lines.Length && IsTableRow(line) && IsTableSeparator(lines[index + 1]))
            {
                index = AddTable(
                    document,
                    lines,
                    index,
                    border,
                    mutedSurface,
                    foreground,
                    secondary,
                    monoFontFamily,
                    mutedSurface);
                continue;
            }
            var heading = HeadingPattern().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                var paragraph = Paragraph(level == 1 ? 19 : level == 2 ? 17 : 15, level <= 2 ? FontWeights.SemiBold : FontWeights.Medium);
                AddInlines(paragraph.Inlines, heading.Groups[2].Value, monoFontFamily, mutedSurface);
                document.Blocks.Add(paragraph);
                index++;
                continue;
            }
            if (HorizontalRulePattern().IsMatch(line))
            {
                document.Blocks.Add(new Paragraph
                {
                    BorderBrush = border,
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Margin = new Thickness(0, 7, 0, 13),
                });
                index++;
                continue;
            }
            if (line.TrimStart().StartsWith('>'))
            {
                index = AddQuote(document, lines, index, secondary, border, monoFontFamily, mutedSurface);
                continue;
            }
            var listMatch = ListPattern().Match(line);
            if (listMatch.Success)
            {
                index = AddList(document, lines, index, listMatch.Groups[1].Value.EndsWith('.'), monoFontFamily, mutedSurface);
                continue;
            }
            index = AddParagraph(document, lines, index, monoFontFamily, mutedSurface);
        }
        return document;
    }

    private static int AddCodeBlock(FlowDocument document, string[] lines, int start, FontFamily mono, Brush background)
    {
        var code = new StringBuilder();
        var index = start + 1;
        while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            if (code.Length > 0) code.AppendLine();
            code.Append(lines[index]);
            index++;
        }
        document.Blocks.Add(new Paragraph(new Run(code.ToString()))
        {
            FontFamily = mono,
            FontSize = 12.5,
            Background = background,
            Padding = new Thickness(11, 9, 11, 9),
            Margin = new Thickness(0, 3, 0, 12),
        });
        return index < lines.Length ? index + 1 : index;
    }

    private static int AddTable(
        FlowDocument document,
        string[] lines,
        int start,
        Brush border,
        Brush headerBackground,
        Brush foreground,
        Brush secondary,
        FontFamily mono,
        Brush inlineCodeBackground)
    {
        var headers = SplitTableRow(lines[start]);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 13) };
        for (var column = 0; column < headers.Count; column++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        group.Rows.Add(CreateTableRow(headers, true, border, headerBackground, foreground, secondary, mono, inlineCodeBackground));
        var index = start + 2;
        while (index < lines.Length && IsTableRow(lines[index]) && !string.IsNullOrWhiteSpace(lines[index]))
        {
            var cells = SplitTableRow(lines[index]);
            while (cells.Count < headers.Count) cells.Add(string.Empty);
            if (cells.Count > headers.Count) cells.RemoveRange(headers.Count, cells.Count - headers.Count);
            group.Rows.Add(CreateTableRow(cells, false, border, Brushes.Transparent, foreground, secondary, mono, inlineCodeBackground));
            index++;
        }
        document.Blocks.Add(table);
        return index;
    }

    private static TableRow CreateTableRow(
        IReadOnlyList<string> cells,
        bool isHeader,
        Brush border,
        Brush background,
        Brush foreground,
        Brush secondary,
        FontFamily mono,
        Brush inlineCodeBackground)
    {
        var row = new TableRow { Background = background };
        foreach (var cellText in cells)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                Foreground = isHeader ? secondary : foreground,
            };
            if (isHeader) paragraph.FontWeight = FontWeights.SemiBold;
            AddInlines(paragraph.Inlines, cellText, mono, inlineCodeBackground);
            row.Cells.Add(new TableCell(paragraph)
            {
                BorderBrush = border,
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(8, 6, 8, 6),
            });
        }
        return row;
    }

    private static int AddQuote(
        FlowDocument document,
        string[] lines,
        int start,
        Brush foreground,
        Brush border,
        FontFamily mono,
        Brush codeBackground)
    {
        var text = new StringBuilder();
        var index = start;
        while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
        {
            if (text.Length > 0) text.AppendLine();
            text.Append(lines[index].TrimStart()[1..].TrimStart());
            index++;
        }
        var paragraph = new Paragraph
        {
            Foreground = foreground,
            BorderBrush = border,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(11, 3, 0, 3),
            Margin = new Thickness(0, 3, 0, 12),
        };
        AddInlines(paragraph.Inlines, text.ToString(), mono, codeBackground);
        document.Blocks.Add(paragraph);
        return index;
    }

    private static int AddList(FlowDocument document, string[] lines, int start, bool ordered, FontFamily mono, Brush codeBackground)
    {
        var list = new List
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(18, 2, 0, 12),
            Padding = new Thickness(4, 0, 0, 0),
        };
        var index = start;
        while (index < lines.Length)
        {
            var match = ListPattern().Match(lines[index]);
            if (!match.Success || match.Groups[1].Value.EndsWith('.') != ordered) break;
            var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 3) };
            AddInlines(paragraph.Inlines, match.Groups[2].Value, mono, codeBackground);
            list.ListItems.Add(new ListItem(paragraph));
            index++;
        }
        document.Blocks.Add(list);
        return index;
    }

    private static int AddParagraph(FlowDocument document, string[] lines, int start, FontFamily mono, Brush codeBackground)
    {
        var text = new StringBuilder();
        var index = start;
        while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
        {
            if (index != start && IsBlockStart(lines, index)) break;
            if (text.Length > 0) text.AppendLine();
            text.Append(lines[index].Trim());
            index++;
        }
        var paragraph = Paragraph(14, FontWeights.Normal);
        AddInlines(paragraph.Inlines, text.ToString(), mono, codeBackground);
        document.Blocks.Add(paragraph);
        return index;
    }

    private static bool IsBlockStart(string[] lines, int index) =>
        lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal) ||
        lines[index].TrimStart().StartsWith('>') ||
        HeadingPattern().IsMatch(lines[index]) ||
        HorizontalRulePattern().IsMatch(lines[index]) ||
        ListPattern().IsMatch(lines[index]) ||
        index + 1 < lines.Length && IsTableRow(lines[index]) && IsTableSeparator(lines[index + 1]);

    private static Paragraph Paragraph(double fontSize, FontWeight fontWeight) => new()
    {
        FontSize = fontSize,
        FontWeight = fontWeight,
        Margin = new Thickness(0, 0, 0, 10),
    };

    private static void AddInlines(InlineCollection inlines, string text, FontFamily mono, Brush codeBackground)
    {
        for (var index = 0; index < text.Length;)
        {
            if (text[index] == '\n')
            {
                inlines.Add(new LineBreak());
                index++;
                continue;
            }
            if (TryLink(text, index, out var label, out var address, out var linkNext))
            {
                inlines.Add(new Run(label) { TextDecorations = TextDecorations.Underline });
                inlines.Add(new Run($" ({address})"));
                index = linkNext;
                continue;
            }
            if (TryDelimited(text, index, "**", out var content, out var next) ||
                TryDelimited(text, index, "__", out content, out next))
            {
                inlines.Add(new Bold(new Run(content)));
                index = next;
                continue;
            }
            if (TryDelimited(text, index, "`", out content, out next))
            {
                inlines.Add(new Run(content) { FontFamily = mono, FontSize = 12.5, Background = codeBackground });
                index = next;
                continue;
            }
            if (TryDelimited(text, index, "~~", out content, out next))
            {
                inlines.Add(new Run(content) { TextDecorations = TextDecorations.Strikethrough });
                index = next;
                continue;
            }
            if (TryDelimited(text, index, "*", out content, out next) ||
                TryDelimited(text, index, "_", out content, out next))
            {
                inlines.Add(new Italic(new Run(content)));
                index = next;
                continue;
            }
            var special = FindNextSpecial(text, index + 1);
            inlines.Add(new Run(text[index..special]));
            index = special;
        }
    }

    private static bool TryLink(
        string text,
        int start,
        out string label,
        out string address,
        out int next)
    {
        label = string.Empty;
        address = string.Empty;
        next = start;
        if (text[start] != '[') return false;
        var labelEnd = text.IndexOf("](", start + 1, StringComparison.Ordinal);
        if (labelEnd <= start + 1) return false;
        var addressEnd = text.IndexOf(')', labelEnd + 2);
        if (addressEnd <= labelEnd + 2) return false;
        var candidate = text[(labelEnd + 2)..addressEnd];
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (!StringComparer.OrdinalIgnoreCase.Equals(uri.Scheme, Uri.UriSchemeHttp) &&
             !StringComparer.OrdinalIgnoreCase.Equals(uri.Scheme, Uri.UriSchemeHttps))) return false;
        label = text[(start + 1)..labelEnd];
        address = uri.AbsoluteUri;
        next = addressEnd + 1;
        return true;
    }

    private static bool TryDelimited(string text, int start, string delimiter, out string content, out int next)
    {
        content = string.Empty;
        next = start;
        if (!text.AsSpan(start).StartsWith(delimiter, StringComparison.Ordinal)) return false;
        var end = text.IndexOf(delimiter, start + delimiter.Length, StringComparison.Ordinal);
        if (end <= start + delimiter.Length) return false;
        content = text[(start + delimiter.Length)..end];
        next = end + delimiter.Length;
        return true;
    }

    private static int FindNextSpecial(string text, int start)
    {
        for (var index = start; index < text.Length; index++)
            if (text[index] is '\n' or '[' or '*' or '_' or '`' or '~') return index;
        return text.Length;
    }

    private static bool IsTableRow(string line) => SplitTableRow(line).Count >= 2;

    private static bool IsTableSeparator(string line)
    {
        var cells = SplitTableRow(line);
        return cells.Count >= 2 && cells.All(cell => TableSeparatorCellPattern().IsMatch(cell));
    }

    internal static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        var cells = new List<string>();
        var cell = new StringBuilder();
        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            if (character == '\\' && index + 1 < trimmed.Length && trimmed[index + 1] is '|' or '\\')
            {
                cell.Append(trimmed[index + 1]);
                index++;
            }
            else if (character == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else cell.Append(character);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static string Normalize(string markdown) => markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    [GeneratedRegex(@"^\s*(#{1,6})\s+(.+?)\s*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^\s*([-+*]|\d+\.)\s+(.+?)\s*$")]
    private static partial Regex ListPattern();

    [GeneratedRegex(@"^\s*([-*_])(?:\s*\1){2,}\s*$")]
    private static partial Regex HorizontalRulePattern();

    [GeneratedRegex(@"^\s*:?-{3,}:?\s*$")]
    private static partial Regex TableSeparatorCellPattern();
}

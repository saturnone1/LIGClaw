using System.Runtime.ExceptionServices;
using System.Windows.Documents;
using System.Windows.Media;
using LIGClaw.Desktop.Infrastructure.Shell;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace LIGClaw.Desktop.Tests;

public sealed class MarkdownDocumentRendererTests
{
    [Fact]
    public void Renders_markdown_table_as_WPF_table_without_raw_delimiters()
    {
        const string markdown = """
            | 볼륨 | 파일 시스템 | 사용률 |
            |---|---|---:|
            | C:\ | NTFS | **68.5 %** |
            | D:\ | NTFS | 64.5 % |
            """;

        var result = RunSta(() =>
        {
            var document = Render(markdown);
            var table = Assert.Single(document.Blocks.OfType<Table>());
            var rows = Assert.Single(table.RowGroups).Rows;
            return new
            {
                Count = rows.Count,
                Header = CellText(rows[0].Cells[0]),
                Root = CellText(rows[1].Cells[0]),
                Usage = CellText(rows[1].Cells[2]),
                BodyForeground = ((Paragraph)rows[1].Cells[0].Blocks.FirstBlock).Foreground,
            };
        });

        Assert.Equal(3, result.Count);
        Assert.Equal("볼륨", result.Header);
        Assert.Equal("C:\\", result.Root);
        Assert.Equal("68.5 %", result.Usage);
        Assert.Same(Brushes.Black, result.BodyForeground);
    }

    [Fact]
    public void Renders_headings_lists_quotes_code_and_inline_emphasis()
    {
        const string markdown = """
            ## 상태

            - 첫 번째
            - 두 번째

            > 참고 내용

            값은 **굵게**, *기울임*, `코드`입니다.

            ```text
            raw code
            ```
            """;

        var result = RunSta(() =>
        {
            var document = Render(markdown);
            var paragraphs = document.Blocks.OfType<Paragraph>().ToArray();
            return new
            {
                HasHeading = paragraphs.Any(paragraph => paragraph.FontSize == 17),
                ListCount = document.Blocks.OfType<List>().Count(),
                HasQuote = paragraphs.Any(paragraph => paragraph.BorderThickness.Left == 3),
                HasBold = paragraphs.Any(paragraph => paragraph.Inlines.OfType<Bold>().Any()),
                HasCode = paragraphs.Any(paragraph => paragraph.FontFamily.Source.Contains("Consolas", StringComparison.Ordinal)),
            };
        });

        Assert.True(result.HasHeading);
        Assert.Equal(1, result.ListCount);
        Assert.True(result.HasQuote);
        Assert.True(result.HasBold);
        Assert.True(result.HasCode);
    }

    [Fact]
    public void Splits_escaped_table_pipes_without_creating_extra_cells()
    {
        var cells = MarkdownDocumentRenderer.SplitTableRow("| 이름 | A\\|B | 값 |");

        Assert.Equal(["이름", "A|B", "값"], cells);
    }

    [Fact]
    public void Renders_http_links_as_readable_selectable_text_without_navigation()
    {
        var result = RunSta(() =>
        {
            var document = Render("공식 [도움말](https://example.com/help?q=1)을 확인하세요.");
            var paragraph = Assert.Single(document.Blocks.OfType<Paragraph>());
            return new
            {
                Text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim(),
                HasHyperlink = paragraph.Inlines.OfType<Hyperlink>().Any(),
                HasUnderlinedLabel = paragraph.Inlines.OfType<Run>().Any(run =>
                    run.Text == "도움말" && run.TextDecorations.Count > 0),
            };
        });

        Assert.Contains("도움말 (https://example.com/help?q=1)", result.Text, StringComparison.Ordinal);
        Assert.False(result.HasHyperlink);
        Assert.True(result.HasUnderlinedLabel);
    }

    private static FlowDocument Render(string markdown) => MarkdownDocumentRenderer.Render(
        markdown,
        new FontFamily("Segoe UI"),
        Brushes.Black,
        Brushes.DimGray,
        Brushes.LightGray,
        Brushes.Gainsboro,
        new FontFamily("Consolas"));

    private static string CellText(TableCell cell) => new TextRange(cell.ContentStart, cell.ContentEnd).Text.Trim();

    private static T RunSta<T>(Func<T> action)
    {
        T? result = default;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                error = ExceptionDispatchInfo.Capture(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
        return result!;
    }
}

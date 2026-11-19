using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace SmartCon.UI.Converters;

/// <summary>
/// Renders CommonMark/GFM markdown (as produced by the LLM) into a
/// theme-aware WPF <see cref="FlowDocument"/> using the official Markdig AST.
/// No HTML/WebView involved.
/// </summary>
public static class MarkdownFlowDocumentRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly FontFamily BodyFont = new("Segoe UI");
    private static readonly FontFamily CodeFont = new("Consolas");
    private static readonly Brush CodeBackground = Frozen(Color.FromArgb(0x1F, 0x80, 0x80, 0x80));
    private static readonly Brush LineBrush = Frozen(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
    private static readonly Brush LinkBrush = Frozen(Color.FromRgb(0x6C, 0xB6, 0xFF));

    public static FlowDocument Render(string? markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            FontFamily = BodyFont,
            FontSize = 12,
            TextAlignment = TextAlignment.Left
        };

        if (string.IsNullOrWhiteSpace(markdown))
            return document;

        var ast = Markdown.Parse(markdown!, Pipeline);
        AppendBlocks(document.Blocks, ast);
        return document;
    }

    private static void AppendBlocks(BlockCollection target, ContainerBlock container)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    AppendHeading(target, heading);
                    break;

                case ParagraphBlock paragraph:
                    AppendParagraph(target, paragraph);
                    break;

                case ListBlock list:
                    AppendList(target, list);
                    break;

                case FencedCodeBlock fenced:
                    AppendCodeBlock(target, fenced.Lines.ToString());
                    break;

                case CodeBlock code:
                    AppendCodeBlock(target, code.Lines.ToString());
                    break;

                case QuoteBlock quote:
                    AppendQuote(target, quote);
                    break;

                case MdTable table:
                    AppendTable(target, table);
                    break;

                case ThematicBreakBlock:
                    target.Add(new Paragraph
                    {
                        BorderBrush = LineBrush,
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Margin = new Thickness(0, 6, 0, 8)
                    });
                    break;
            }
        }
    }

    private static void AppendHeading(BlockCollection target, HeadingBlock heading)
    {
        var paragraph = new Paragraph
        {
            FontWeight = FontWeights.Bold,
            FontSize = heading.Level switch
            {
                1 => 16,
                2 => 15,
                3 => 14,
                _ => 12
            },
            Margin = new Thickness(0, heading.Level <= 2 ? 8 : 6, 0, 3),
            LineHeight = 20
        };

        AppendInlines(paragraph, heading.Inline);
        target.Add(paragraph);
    }

    private static void AppendParagraph(BlockCollection target, ParagraphBlock source)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 5),
            LineHeight = 18
        };

        AppendInlines(paragraph, source.Inline);
        target.Add(paragraph);
    }

    private static void AppendList(BlockCollection target, ListBlock source)
    {
        var list = new List
        {
            MarkerStyle = source.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 5),
            Padding = new Thickness(16, 0, 0, 0)
        };

        if (source.IsOrdered && source.OrderedStart is { } start && int.TryParse(start, out var startIndex))
            list.StartIndex = startIndex;

        foreach (var item in source)
        {
            var listItem = new ListItem();
            if (item is ContainerBlock container)
                AppendBlocks(listItem.Blocks, container);

            if (listItem.Blocks.Count == 0)
                listItem.Blocks.Add(new Paragraph());

            list.ListItems.Add(listItem);
        }

        target.Add(list);
    }

    private static void AppendQuote(BlockCollection target, QuoteBlock source)
    {
        var section = new Section
        {
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(8, 0, 0, 0),
            Margin = new Thickness(0, 2, 0, 6)
        };

        AppendBlocks(section.Blocks, source);
        target.Add(section);
    }

    private static void AppendCodeBlock(BlockCollection target, string code)
    {
        var paragraph = new Paragraph
        {
            FontFamily = CodeFont,
            FontSize = 11,
            LineHeight = 15,
            Background = CodeBackground,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 2, 0, 6)
        };

        var lines = code.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                paragraph.Inlines.Add(new LineBreak());

            paragraph.Inlines.Add(new Run(lines[i]));
        }

        target.Add(paragraph);
    }

    private static void AppendTable(BlockCollection target, MdTable source)
    {
        var table = new WpfTable
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 2, 0, 8)
        };

        var rows = new List<MdTableRow>();
        foreach (var row in source)
        {
            if (row is MdTableRow tableRow)
                rows.Add(tableRow);
        }

        var columnCount = 0;
        foreach (var row in rows)
            columnCount = Math.Max(columnCount, row.Count);

        for (var i = 0; i < columnCount; i++)
            table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        foreach (var sourceRow in rows)
        {
            var row = new WpfTableRow();
            foreach (var cell in sourceRow)
            {
                if (cell is not MdTableCell sourceCell)
                    continue;

                var tableCell = new WpfTableCell
                {
                    BorderBrush = LineBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(5, 3, 5, 3)
                };

                AppendBlocks(tableCell.Blocks, sourceCell);
                if (sourceRow.IsHeader)
                {
                    foreach (var paragraph in tableCell.Blocks.OfType<Paragraph>())
                        paragraph.FontWeight = FontWeights.SemiBold;
                }

                row.Cells.Add(tableCell);
            }

            group.Rows.Add(row);
        }

        target.Add(table);
    }

    private static void AppendInlines(Paragraph paragraph, ContainerInline? container) =>
        AppendInlines(paragraph, container, FontWeights.Normal, FontStyles.Normal);

    private static void AppendInlines(
        Paragraph paragraph,
        ContainerInline? container,
        FontWeight weight,
        FontStyle style,
        TextDecorationCollection? decorations = null)
    {
        if (container is null)
            return;

        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
            AppendInline(paragraph, inline, weight, style, decorations);
    }

    private static void AppendInline(
        Paragraph paragraph,
        MdInline inline,
        FontWeight weight,
        FontStyle style,
        TextDecorationCollection? decorations)
    {
        switch (inline)
        {
            case LiteralInline literal:
                paragraph.Inlines.Add(new Run(literal.Content.ToString())
                {
                    FontWeight = weight,
                    FontStyle = style,
                    TextDecorations = decorations
                });
                break;

            case EmphasisInline emphasis:
                var isBold = emphasis.DelimiterCount >= 2;
                var isStrike = emphasis.DelimiterChar == '~';
                AppendInlines(
                    paragraph,
                    emphasis,
                    isBold ? FontWeights.Bold : weight,
                    isBold ? style : FontStyles.Italic,
                    isStrike ? TextDecorations.Strikethrough : decorations);
                break;

            case CodeInline code:
                paragraph.Inlines.Add(new Run(code.Content)
                {
                    FontFamily = CodeFont,
                    Background = CodeBackground
                });
                break;

            case LinkInline link when link.IsImage:
                paragraph.Inlines.Add(new Run("[image: " + (link.Title ?? link.Url ?? "image") + "]")
                {
                    FontStyle = FontStyles.Italic
                });
                break;

            case LinkInline link:
                var linkText = GetPlainText(link);
                var hyperlink = new Hyperlink(new Run(
                    string.IsNullOrWhiteSpace(linkText) ? link.Url ?? string.Empty : linkText))
                {
                    FontWeight = weight,
                    FontStyle = style,
                    Foreground = LinkBrush
                };

                if (TryCreateHttpUri(link.Url, out var uri))
                {
                    hyperlink.NavigateUri = uri;
                    hyperlink.RequestNavigate += OnRequestNavigate;
                }

                paragraph.Inlines.Add(hyperlink);
                break;

            case AutolinkInline autolink:
                var autoLink = new Hyperlink(new Run(autolink.Url ?? string.Empty))
                {
                    FontWeight = weight,
                    FontStyle = style,
                    Foreground = LinkBrush
                };

                if (TryCreateHttpUri(autolink.Url, out var autoUri))
                {
                    autoLink.NavigateUri = autoUri;
                    autoLink.RequestNavigate += OnRequestNavigate;
                }

                paragraph.Inlines.Add(autoLink);
                break;

            case LineBreakInline:
                paragraph.Inlines.Add(new LineBreak());
                break;

            case HtmlInline html:
                paragraph.Inlines.Add(new Run(html.Tag)
                {
                    FontFamily = CodeFont
                });
                break;

            case ContainerInline nested:
                AppendInlines(paragraph, nested, weight, style, decorations);
                break;
        }
    }

    private static string GetPlainText(ContainerInline container)
    {
        var builder = new System.Text.StringBuilder();
        AppendPlainText(builder, container);
        return builder.ToString();
    }

    private static void AppendPlainText(System.Text.StringBuilder builder, ContainerInline container)
    {
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case ContainerInline nested:
                    AppendPlainText(builder, nested);
                    break;
            }
        }
    }

    private static bool TryCreateHttpUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;

        uri = parsed;
        return true;
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
        }

        e.Handled = true;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
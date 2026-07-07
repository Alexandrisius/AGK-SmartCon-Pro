using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using SmartCon.Core.Services.FamilyManager;

namespace SmartCon.UI.Converters;

/// <summary>
/// Конвертер для inline-подсветки символов в TreeView FamilyManager при активном поиске.
/// Принимает MultiBinding из двух значений:
///   [0] — отображаемый текст (DisplayName семейства или типа),
///   [1] — строка поиска (SearchText, может содержать несколько токенов через пробел).
/// Возвращает <see cref="TextBlock"/> с чередующимися <see cref="Run"/>: обычные и
/// подсвеченные (background + SemiBold). Цвет текста НЕ меняется — подсвеченный
/// Run наследует Foreground от родительского TextBlock/ContentControl, как в Project
/// Browser самого Revit. Все совпадения подсвечиваются регистронезависимо;
/// перекрывающиеся диапазоны объединяются.
/// Использует <see cref="FamilySearchNormalizer.Tokenize"/> для разбиения строки поиска
/// на токены — поведение совпадает с SQL-фильтром в
/// <c>LocalCatalogQueryBuilder.BuildWhereClause</c>.
/// Цвет фона совпадает с <c>SelectedSurfaceBrush</c> из SmartConTheme
/// (Material Design Blue 50): семантически «найдено» = «выделено»,
/// тот же токен что и selected/hover items в VS WPF guidelines.
/// </summary>
[ValueConversion(typeof(object[]), typeof(TextBlock))]
public sealed class SearchHighlightConverter : IMultiValueConverter
{
    private static readonly Brush HighlightBackground = CreateFrozenBrush(Color.FromRgb(0xE3, 0xF2, 0xFD));

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }
        return brush;
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2)
        {
            return values is { Length: > 0 } ? values[0] ?? string.Empty : string.Empty;
        }

        var text = values[0] as string;
        var search = values[1] as string;

        if (text is null || search is null || string.IsNullOrWhiteSpace(search))
        {
            return text ?? string.Empty;
        }

        var tokens = FamilySearchNormalizer.Tokenize(search);
        if (tokens.Count == 0)
        {
            return text;
        }

        // IsHitTestVisible=false lets mouse clicks pass through the TextBlock to the
        // owning TreeViewItem. Without this, Runs with a non-null Background absorb
        // hit-test results, so SelectedItem never updates and drag never starts on
        // search results. The hit-test in TreeViewDragDropBehavior uses the cursor
        // position instead of SelectedItem, but click-to-select still benefits.
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            IsHitTestVisible = false,
        };
        AppendHighlightedRuns(textBlock, text, tokens);
        return textBlock;
    }

    private static void AppendHighlightedRuns(TextBlock textBlock, string text, IReadOnlyList<string> tokens)
    {
        var ranges = new List<(int Start, int End)>();

        foreach (var token in tokens)
        {
            if (token.Length == 0)
            {
                continue;
            }

            int idx = 0;
            while (idx <= text.Length - token.Length)
            {
                int found = text.IndexOf(token, idx, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    break;
                }
                ranges.Add((found, found + token.Length));
                idx = found + token.Length;
            }
        }

        if (ranges.Count == 0)
        {
            textBlock.Inlines.Add(new Run(text));
            return;
        }

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<(int Start, int End)>(ranges.Count);
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Start <= merged[merged.Count - 1].End)
            {
                var last = merged[merged.Count - 1];
                merged[merged.Count - 1] = (last.Start, Math.Max(last.End, range.End));
            }
            else
            {
                merged.Add(range);
            }
        }

        int cursor = 0;
        foreach (var (start, end) in merged)
        {
            if (start > cursor)
            {
                textBlock.Inlines.Add(new Run(text.Substring(cursor, start - cursor)));
            }

            var highlightRun = new Run(text.Substring(start, end - start))
            {
                Background = HighlightBackground,
                FontWeight = FontWeights.SemiBold,
            };
            textBlock.Inlines.Add(highlightRun);
            cursor = end;
        }

        if (cursor < text.Length)
        {
            textBlock.Inlines.Add(new Run(text.Substring(cursor)));
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

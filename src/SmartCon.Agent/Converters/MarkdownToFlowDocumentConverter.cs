using System.Globalization;
using System.Windows.Data;
using SmartCon.Agent.Services.UI;

namespace SmartCon.Agent.Converters;

public sealed class MarkdownToFlowDocumentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string)
            ? null
            : MarkdownFlowDocumentRenderer.Render(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
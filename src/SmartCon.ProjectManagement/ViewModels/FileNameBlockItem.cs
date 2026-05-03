using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models;

namespace SmartCon.ProjectManagement.ViewModels;

public sealed partial class FileNameBlockItem : ObservableObject
{
    [ObservableProperty]
    private int _index;

    [ObservableProperty]
    private string _field = string.Empty;

    [ObservableProperty]
    private ParseRule _parseRule = new();

    [ObservableProperty]
    private string _currentFieldValue = string.Empty;

    [ObservableProperty]
    private bool _isValid = true;

    [ObservableProperty]
    private string? _validationError;

    public string ParseRuleDisplay => GetParseRuleDisplay();

    private string GetParseRuleDisplay()
    {
        return ParseRule.Mode switch
        {
            ParseMode.DelimiterSegment => ParseRule.SegmentCount > 1
                ? $"Разд. [{ParseRule.Delimiter}] #{ParseRule.SegmentIndex}-{ParseRule.SegmentIndex + ParseRule.SegmentCount - 1}"
                : $"Разд. [{ParseRule.Delimiter}] #{ParseRule.SegmentIndex}",
            ParseMode.FixedWidth => ParseRule.CharOffset > 0
                ? $"Фикс. +{ParseRule.CharOffset} ×{ParseRule.CharCount}"
                : $"{ParseRule.CharCount} симв.",
            ParseMode.BetweenMarkers =>
                FormatBetweenDisplay(),
            ParseMode.AfterMarker => ParseRule.MarkerIndex > 1
                ? $"После [{ParseRule.Marker}] №{ParseRule.MarkerIndex}"
                : $"После [{ParseRule.Marker}]",
            ParseMode.Remainder =>
                "Остаток",
            _ => ParseRule.Mode.ToString()
        };
    }

    private string FormatBetweenDisplay()
    {
        var open = ParseRule.OpenMarker;
        var close = ParseRule.CloseMarker;
        var hasOpenIdx = ParseRule.OpenMarkerIndex > 1;
        var hasCloseIdx = ParseRule.CloseMarkerIndex > 1;

        if (!hasOpenIdx && !hasCloseIdx)
            return $"{open} ... {close}";

        var parts = new List<string>();
        if (hasOpenIdx) parts.Add($"открывающий №{ParseRule.OpenMarkerIndex}");
        if (hasCloseIdx) parts.Add($"закрывающий №{ParseRule.CloseMarkerIndex}");

        return $"{open} ... {close} ({string.Join(", ", parts)})";
    }

    public void RefreshParseRuleDisplay()
    {
        OnPropertyChanged(nameof(ParseRuleDisplay));
    }
}

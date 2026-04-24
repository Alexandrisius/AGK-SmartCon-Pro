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
            ParseMode.DelimiterSegment =>
                $"Разд. [{ParseRule.Delimiter}] #{ParseRule.SegmentIndex}",
            ParseMode.FixedWidth =>
                $"{ParseRule.CharCount} симв.",
            ParseMode.BetweenMarkers =>
                $"{ParseRule.OpenMarker} ... {ParseRule.CloseMarker}",
            ParseMode.AfterMarker =>
                $"После [{ParseRule.Marker}]",
            ParseMode.Remainder =>
                "Остаток",
            _ => ParseRule.Mode.ToString()
        };
    }

    public void RefreshParseRuleDisplay()
    {
        OnPropertyChanged(nameof(ParseRuleDisplay));
    }
}

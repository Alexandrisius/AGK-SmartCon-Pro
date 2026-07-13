using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models;
using SmartCon.Core.Services;

namespace SmartCon.FamilyManager.ViewModels.ProjectBase;

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
                ? $"{LocalizationService.GetString("FM_PBase_ParseRule_Delimiter") ?? "Delimiter"} [{ParseRule.Delimiter}] #{ParseRule.SegmentIndex}-{ParseRule.SegmentIndex + ParseRule.SegmentCount - 1}"
                : $"{LocalizationService.GetString("FM_PBase_ParseRule_Delimiter") ?? "Delimiter"} [{ParseRule.Delimiter}] #{ParseRule.SegmentIndex}",
            ParseMode.FixedWidth => ParseRule.CharOffset > 0
                ? $"{LocalizationService.GetString("FM_PBase_ParseRule_FixedOffset") ?? "Fixed offset"} +{ParseRule.CharOffset} ×{ParseRule.CharCount}"
                : $"{LocalizationService.GetString("FM_PBase_ParseRule_Fixed") ?? "Fixed"} {ParseRule.CharCount} {LocalizationService.GetString("FM_PBase_ParseRule_Chars") ?? "chars"}",
            ParseMode.BetweenMarkers => FormatBetweenDisplay(),
            ParseMode.AfterMarker => ParseRule.MarkerIndex > 1
                ? $"{LocalizationService.GetString("FM_PBase_ParseRule_After") ?? "After"} [{ParseRule.Marker}] #{ParseRule.MarkerIndex}"
                : $"{LocalizationService.GetString("FM_PBase_ParseRule_After") ?? "After"} [{ParseRule.Marker}]",
            ParseMode.Remainder => LocalizationService.GetString("FM_PBase_ParseRule_Remainder") ?? "Remainder",
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
        if (hasOpenIdx) parts.Add(string.Format(LocalizationService.GetString("FM_PBase_ParseRule_OpeningN") ?? "opening #{0}", ParseRule.OpenMarkerIndex));
        if (hasCloseIdx) parts.Add(string.Format(LocalizationService.GetString("FM_PBase_ParseRule_ClosingN") ?? "closing #{0}", ParseRule.CloseMarkerIndex));

        return $"{open} ... {close} ({string.Join(", ", parts)})";
    }

    public void RefreshParseRuleDisplay()
    {
        OnPropertyChanged(nameof(ParseRuleDisplay));
    }
}

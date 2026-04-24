using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.ProjectManagement.ViewModels;

public sealed partial class ParseRuleViewModel : ObservableObject, IObservableRequestClose
{
    [ObservableProperty]
    private ParseMode _mode = ParseMode.DelimiterSegment;

    [ObservableProperty]
    private string _delimiter = "-";

    [ObservableProperty]
    private int _segmentIndex;

    [ObservableProperty]
    private int _charCount = 3;

    [ObservableProperty]
    private string _openMarker = "(";

    [ObservableProperty]
    private string _closeMarker = ")";

    [ObservableProperty]
    private string _marker = "-";

    [ObservableProperty]
    private string _previewFileName = string.Empty;

    [ObservableProperty]
    private string _previewValue = string.Empty;

    [ObservableProperty]
    private string _previewRemaining = string.Empty;

    public List<EnumOption<ParseMode>> ParseModeOptions { get; }

    private EnumOption<ParseMode> _selectedParseModeOption;

    public EnumOption<ParseMode> SelectedParseModeOption
    {
        get => _selectedParseModeOption;
        set
        {
            if (SetProperty(ref _selectedParseModeOption, value) && value is not null)
                Mode = value.Value;
        }
    }

    private readonly List<ParseRule> _precedingRules;

    public event Action<bool?>? RequestClose;

    public bool IsDelimiterSegment => Mode == ParseMode.DelimiterSegment;
    public bool IsFixedWidth => Mode == ParseMode.FixedWidth;
    public bool IsBetweenMarkers => Mode == ParseMode.BetweenMarkers;
    public bool IsAfterMarker => Mode == ParseMode.AfterMarker;
    public bool IsRemainder => Mode == ParseMode.Remainder;

    public ParseRuleViewModel(ParseRule initialRule, string previewFileName, List<ParseRule> precedingRules)
    {
        _mode = initialRule.Mode;
        _delimiter = initialRule.Delimiter;
        _segmentIndex = initialRule.SegmentIndex;
        _charCount = initialRule.CharCount;
        _openMarker = initialRule.OpenMarker;
        _closeMarker = initialRule.CloseMarker;
        _marker = initialRule.Marker;
        _previewFileName = previewFileName;
        _precedingRules = precedingRules;

        ParseModeOptions =
        [
            new() { Value = ParseMode.DelimiterSegment, Display = LocalizationService.GetString("PM_ParseMode_Delimiter"), Description = LocalizationService.GetString("PM_ParseMode_Delimiter_Desc") },
            new() { Value = ParseMode.FixedWidth, Display = LocalizationService.GetString("PM_ParseMode_FixedWidth"), Description = LocalizationService.GetString("PM_ParseMode_FixedWidth_Desc") },
            new() { Value = ParseMode.BetweenMarkers, Display = LocalizationService.GetString("PM_ParseMode_Between"), Description = LocalizationService.GetString("PM_ParseMode_Between_Desc") },
            new() { Value = ParseMode.AfterMarker, Display = LocalizationService.GetString("PM_ParseMode_After"), Description = LocalizationService.GetString("PM_ParseMode_After_Desc") },
            new() { Value = ParseMode.Remainder, Display = LocalizationService.GetString("PM_ParseMode_Remainder"), Description = LocalizationService.GetString("PM_ParseMode_Remainder_Desc") }
        ];

        _selectedParseModeOption = ParseModeOptions.First(o => o.Value == _mode);

        RefreshModeVisibility();
        RefreshPreview();
    }

    partial void OnModeChanged(ParseMode value)
    {
        _selectedParseModeOption = ParseModeOptions.First(o => o.Value == value);
        OnPropertyChanged(nameof(SelectedParseModeOption));
        RefreshModeVisibility();
        RefreshPreview();
    }

    partial void OnDelimiterChanged(string value) => RefreshPreview();
    partial void OnSegmentIndexChanged(int value) => RefreshPreview();
    partial void OnCharCountChanged(int value) => RefreshPreview();
    partial void OnOpenMarkerChanged(string value) => RefreshPreview();
    partial void OnCloseMarkerChanged(string value) => RefreshPreview();
    partial void OnMarkerChanged(string value) => RefreshPreview();

    private void RefreshModeVisibility()
    {
        OnPropertyChanged(nameof(IsDelimiterSegment));
        OnPropertyChanged(nameof(IsFixedWidth));
        OnPropertyChanged(nameof(IsBetweenMarkers));
        OnPropertyChanged(nameof(IsAfterMarker));
        OnPropertyChanged(nameof(IsRemainder));
    }

    private void RefreshPreview()
    {
        if (string.IsNullOrEmpty(PreviewFileName))
        {
            PreviewValue = string.Empty;
            PreviewRemaining = string.Empty;
            return;
        }

        var remaining = Path.GetFileNameWithoutExtension(PreviewFileName);

        foreach (var prevRule in _precedingRules)
        {
            var (_, newRemaining) = FileNameParser.ApplyParseRule(remaining, prevRule);
            remaining = newRemaining;
        }

        var rule = BuildRule();
        var (value, afterCurrent) = FileNameParser.ApplyParseRule(remaining, rule);
        PreviewValue = value;
        PreviewRemaining = afterCurrent;
    }

    public ParseRule BuildRule()
    {
        return new ParseRule
        {
            Mode = Mode,
            Delimiter = Delimiter,
            SegmentIndex = SegmentIndex,
            CharCount = CharCount,
            OpenMarker = OpenMarker,
            CloseMarker = CloseMarker,
            Marker = Marker
        };
    }

    [RelayCommand]
    private void Ok()
    {
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}

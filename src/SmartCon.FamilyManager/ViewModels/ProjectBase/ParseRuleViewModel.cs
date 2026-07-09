using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.ViewModels.ProjectBase;

public sealed partial class ParseRuleViewModel : ObservableObject, IObservableRequestClose
{
    [ObservableProperty]
    private ParseMode _mode = ParseMode.DelimiterSegment;

    [ObservableProperty]
    private string _delimiter = "-";

    [ObservableProperty]
    private int _segmentIndex = 1;

    [ObservableProperty]
    private int _segmentCount = 1;

    [ObservableProperty]
    private int _charOffset;

    [ObservableProperty]
    private int _charCount = 3;

    [ObservableProperty]
    private string _openMarker = "(";

    [ObservableProperty]
    private int _openMarkerIndex = 1;

    [ObservableProperty]
    private string _closeMarker = ")";

    [ObservableProperty]
    private int _closeMarkerIndex = 1;

    [ObservableProperty]
    private string _marker = "-";

    [ObservableProperty]
    private int _markerIndex = 1;

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
        _segmentCount = initialRule.SegmentCount;
        _charOffset = initialRule.CharOffset;
        _charCount = initialRule.CharCount;
        _openMarker = initialRule.OpenMarker;
        _openMarkerIndex = initialRule.OpenMarkerIndex;
        _closeMarker = initialRule.CloseMarker;
        _closeMarkerIndex = initialRule.CloseMarkerIndex;
        _marker = initialRule.Marker;
        _markerIndex = initialRule.MarkerIndex;
        _previewFileName = previewFileName;
        _precedingRules = precedingRules;

        ParseModeOptions =
        [
            new() { Value = ParseMode.DelimiterSegment, Display = LocalizationService.GetString("FM_PBase_ParseMode_Delimiter") ?? "Delimiter", Description = LocalizationService.GetString("FM_PBase_ParseMode_Delimiter_Desc") ?? "Split by delimiter and pick segment" },
            new() { Value = ParseMode.FixedWidth, Display = LocalizationService.GetString("FM_PBase_ParseMode_FixedWidth") ?? "Fixed width", Description = LocalizationService.GetString("FM_PBase_ParseMode_FixedWidth_Desc") ?? "Take N characters from offset" },
            new() { Value = ParseMode.BetweenMarkers, Display = LocalizationService.GetString("FM_PBase_ParseMode_Between") ?? "Between markers", Description = LocalizationService.GetString("FM_PBase_ParseMode_Between_Desc") ?? "Text between opening and closing markers" },
            new() { Value = ParseMode.AfterMarker, Display = LocalizationService.GetString("FM_PBase_ParseMode_After") ?? "After marker", Description = LocalizationService.GetString("FM_PBase_ParseMode_After_Desc") ?? "Text after a marker" },
            new() { Value = ParseMode.Remainder, Display = LocalizationService.GetString("FM_PBase_ParseMode_Remainder") ?? "Remainder", Description = LocalizationService.GetString("FM_PBase_ParseMode_Remainder_Desc") ?? "Everything left after previous blocks" }
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
    partial void OnSegmentCountChanged(int value) => RefreshPreview();
    partial void OnCharOffsetChanged(int value) => RefreshPreview();
    partial void OnCharCountChanged(int value) => RefreshPreview();
    partial void OnOpenMarkerChanged(string value) => RefreshPreview();
    partial void OnOpenMarkerIndexChanged(int value) => RefreshPreview();
    partial void OnCloseMarkerChanged(string value) => RefreshPreview();
    partial void OnCloseMarkerIndexChanged(int value) => RefreshPreview();
    partial void OnMarkerChanged(string value) => RefreshPreview();
    partial void OnMarkerIndexChanged(int value) => RefreshPreview();

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
            SegmentCount = SegmentCount,
            CharOffset = CharOffset,
            CharCount = CharCount,
            OpenMarker = OpenMarker,
            OpenMarkerIndex = OpenMarkerIndex,
            CloseMarker = CloseMarker,
            CloseMarkerIndex = CloseMarkerIndex,
            Marker = Marker,
            MarkerIndex = MarkerIndex
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

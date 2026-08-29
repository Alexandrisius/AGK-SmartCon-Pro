using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Routing part picker (ADR-072, Phase 3): family + TYPE selection from the
/// whole catalog, filtered by the group's fitting Revit category and
/// part_type ordinals (strictly as the Revit routing dialog filters —
/// param.Set/AddRule reject incompatible parts). The result is the canonical
/// <c>"Family:Type"</c> part token of the routing rule.
/// </summary>
public sealed partial class RoutingPartPickerViewModel : ObservableObject, IObservableRequestClose
{
    private readonly IRoutingEditorService _routingEditorService;
    private readonly int _fittingCategoryId;
    private readonly IReadOnlyCollection<int> _partTypeOrdinals;
    private readonly string? _initialPartName;

    private List<RoutingPartCandidate> _allCandidates = [];

    public RoutingPartPickerViewModel(
        IRoutingEditorService routingEditorService,
        int fittingCategoryId,
        IReadOnlyCollection<int> partTypeOrdinals,
        string? initialPartName)
    {
        _routingEditorService = routingEditorService;
        _fittingCategoryId = fittingCategoryId;
        _partTypeOrdinals = partTypeOrdinals;
        _initialPartName = initialPartName;
    }

    [ObservableProperty] private ObservableCollection<RoutingPartCandidate> _candidates = [];
    [ObservableProperty] private RoutingPartCandidate? _selectedCandidate;
    [ObservableProperty] private ObservableCollection<string> _types = [];
    [ObservableProperty] private string? _selectedType;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasNoCandidates;

    public string SearchWatermark =>
        LanguageManager.GetString(StringLocalization.Keys.FM_Routing_Picker_Search) ?? "Search";
    public string NoCandidatesMessage =>
        LanguageManager.GetString(StringLocalization.Keys.FM_Routing_NoCandidates) ?? string.Empty;

    /// <summary>The picked <c>"Family:Type"</c> token (set on OK).</summary>
    public string? Result { get; private set; }

    public event Action<bool?>? RequestClose;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("RoutingEditor",
            ("Method", nameof(InitializeAsync)),
            ("FittingCategory", _fittingCategoryId),
            ("PartTypes", _partTypeOrdinals.Count));
        IsBusy = true;
        try
        {
            _allCandidates = (await _routingEditorService
                .GetPartCandidatesAsync(_fittingCategoryId, _partTypeOrdinals, ct)
                .ConfigureAwait(true)).ToList();
            HasNoCandidates = _allCandidates.Count == 0;
            ApplyFilter();

            // Preselect the currently assigned part.
            if (_initialPartName is not null)
            {
                var separator = _initialPartName.IndexOf(':');
                if (separator > 0)
                {
                    var family = _initialPartName.Substring(0, separator);
                    var type = _initialPartName.Substring(separator + 1);
                    SelectedCandidate = Candidates.FirstOrDefault(c =>
                        string.Equals(c.FamilyName, family, StringComparison.Ordinal));
                    if (SelectedCandidate is not null)
                    {
                        SelectedType = Types.FirstOrDefault(t =>
                            string.Equals(t, type, StringComparison.Ordinal));
                    }
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedCandidateChanged(RoutingPartCandidate? value)
    {
        Types = new ObservableCollection<string>(value?.TypeNames ?? (IReadOnlyList<string>)[]);
        SelectedType = Types.Count == 1 ? Types[0] : null;
        OkCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTypeChanged(string? value)
    {
        OkCommand.NotifyCanExecuteChanged();
    }

    private void ApplyFilter()
    {
        IEnumerable<RoutingPartCandidate> source = _allCandidates;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
#pragma warning disable CA2249 // IndexOf used for net48 compat — string.Contains(string, StringComparison) is net8+ only
            source = source.Where(c =>
                c.FamilyName.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0);
#pragma warning restore CA2249
        }
        Candidates = new ObservableCollection<RoutingPartCandidate>(source);
    }

    private bool CanOk() => SelectedCandidate is not null && SelectedType is not null;

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        Result = SelectedCandidate!.FamilyName + ":" + SelectedType!;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}

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
    private readonly int _preferredJunctionType;
    private readonly int _connectorShapeBits;
    private readonly int _requiredShapeMask;
    private readonly bool _excludeMultiShape;

    private List<RoutingPartCandidate> _allCandidates = [];

    /// <param name="preferredJunctionType">Junctions-group filter (owner
    /// stress test 2026-09-01): 0 = tee, 1 = tap — candidates of the
    /// NON-preferred junction part type are hidden, exactly like the Revit
    /// routing dialog never applies them. -1 = no junction filter (all
    /// non-junctions groups; param groups already split tee/tap by their
    /// part_type ordinals).</param>
    /// <param name="connectorShapeBits">Host connector-profile bitmask
    /// (Round=1, Rectangular=2, Oval=4; 0 = no filter, owner stress test
    /// 2026-09-01 баг 8): a round flex duct never offers rectangular-only
    /// fittings — Revit silently rejects them at sync.</param>
    /// <param name="requiredShapeMask">Multi-shape transition rows (owner
    /// stress test 2026-09-01): the candidate must carry ALL these bits —
    /// a rect-to-round row never offers a purely rectangular transition.</param>
    /// <param name="excludeMultiShape">Plain Transitions rows (owner stress
    /// test 2026-09-01): single-shape parts only — multi-shape transitions
    /// live in their own dedicated rows.</param>
    public RoutingPartPickerViewModel(
        IRoutingEditorService routingEditorService,
        int fittingCategoryId,
        IReadOnlyCollection<int> partTypeOrdinals,
        string? initialPartName,
        string? contextLabel = null,
        int preferredJunctionType = -1,
        int connectorShapeBits = 0,
        int requiredShapeMask = 0,
        bool excludeMultiShape = false)
    {
        _routingEditorService = routingEditorService;
        _fittingCategoryId = fittingCategoryId;
        _partTypeOrdinals = partTypeOrdinals;
        _initialPartName = initialPartName;
        _preferredJunctionType = preferredJunctionType;
        _connectorShapeBits = connectorShapeBits;
        _requiredShapeMask = requiredShapeMask;
        _excludeMultiShape = excludeMultiShape;

        // The part class is the dialog context (owner decision 2026-08-30):
        // it belongs in the window title, not in every family row.
        var baseTitle = LanguageManager.GetString(StringLocalization.Keys.FM_Routing_Picker_Title) ?? "Select part";
        PickerTitle = string.IsNullOrEmpty(contextLabel)
            ? baseTitle
            : $"{baseTitle} — {contextLabel}";
    }

    /// <summary>Window title: picker purpose + the routing group (part class).</summary>
    public string PickerTitle { get; }

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
                .GetPartCandidatesAsync(_fittingCategoryId, _partTypeOrdinals, _connectorShapeBits, _requiredShapeMask, _excludeMultiShape, ct)
                .ConfigureAwait(true)).ToList();
            // Junctions filter: the group holds both tee and tap part types,
            // but only the preferred junction kind is applicable — offering
            // the other kind guarantees a sync-time rejection by Revit.
            if (_preferredJunctionType >= 0)
            {
                _allCandidates = _allCandidates
                    .Where(c => !RoutingGroupCatalog.IsInactiveJunctionPart(
                        c.PartTypeOrdinal, _preferredJunctionType))
                    .ToList();
            }
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

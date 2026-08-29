using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Routing editor tab (ADR-072, Phase 3): per-type editing of the routing
/// rules stored as catalog data (V34). Visible only for system MEPCurve
/// items (pipe/duct/flex/conduit/cable tray). The group matrix and the part
/// filters mirror the Revit routing dialog (<see cref="RoutingGroupCatalog"/>);
/// the save goes through <see cref="IRoutingEditorService"/> and produces a
/// NEW catalog version with recomputed hashes, sections and dependency
/// links. Rule edits are kept per type, so switching types in the selector
/// never loses pending changes; a save persists every touched type at once.
/// </summary>
public sealed partial class FamilyPropertiesViewModel
{
    private IRoutingEditorService? _routingEditorService;
    private int _routingHostCategoryId;
    private RoutingEditorData? _routingData;

    /// <summary>Per-type edit state (key = familyKey|typeName) — survives type switches.</summary>
    private readonly Dictionary<string, RoutingTypeEditState> _routingEdits = new(StringComparer.Ordinal);
    /// <summary>Original fingerprints for dirty detection (key = familyKey|typeName).</summary>
    private readonly Dictionary<string, string> _routingOriginals = new(StringComparer.Ordinal);
    /// <summary>Dropdown source: «Все» + segment nominal diameters (mm).</summary>
    private IReadOnlyList<string> _routingSizeOptions = [];

    [ObservableProperty] private bool _isRoutingTabVisible;
    [ObservableProperty] private bool _isRoutingLegacy;
    [ObservableProperty] private bool _isRoutingBusy;
    [ObservableProperty] private string? _routingStatusMessage;
    [ObservableProperty] private ObservableCollection<RoutingTypeItem> _routingTypes = [];
    [ObservableProperty] private RoutingTypeItem? _selectedRoutingType;
    [ObservableProperty] private ObservableCollection<RoutingGroupRowViewModel> _routingGroups = [];
    [ObservableProperty] private bool _showPreferredJunction;
    [ObservableProperty] private RoutingJunctionOption? _selectedPreferredJunction;

    public IReadOnlyList<RoutingJunctionOption> PreferredJunctionOptions { get; } =
    [
        new RoutingJunctionOption(0,
            LanguageManager.GetString(StringLocalization.Keys.FM_Routing_Junction_Tee) ?? "Tee"),
        new RoutingJunctionOption(1,
            LanguageManager.GetString(StringLocalization.Keys.FM_Routing_Junction_Tap) ?? "Tap"),
    ];

    /// <summary>Editor area visibility (hidden for legacy versions — only the hint shows).</summary>
    public bool ShowRoutingEditor => IsRoutingTabVisible && !IsRoutingLegacy;

    /// <summary>Routing editing is enabled (not read-only, not legacy).</summary>
    public bool CanEditRouting => !IsReadOnly && !IsRoutingLegacy;

    /// <summary>Status line visibility.</summary>
    public bool HasRoutingStatusMessage => !string.IsNullOrEmpty(RoutingStatusMessage);

    partial void OnRoutingStatusMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasRoutingStatusMessage));
    }

    /// <summary>
    /// Central hint of the locked editor state: the legacy-format message,
    /// or the type-name-collision message (both refuse saves for their own
    /// reason — actualization cures only the first).
    /// </summary>
    public string RoutingLockHint =>
        _routingData?.HasTypeNameCollisions == true
            ? LanguageManager.GetString(StringLocalization.Keys.FM_Routing_DuplicateTypes) ?? string.Empty
            : LanguageManager.GetString(StringLocalization.Keys.FM_Routing_LegacyVersion) ?? string.Empty;

    /// <summary>Any type's current edit state differs from the stored rules.</summary>
    public bool HasRoutingChanges
    {
        get
        {
            if (!IsRoutingTabVisible || IsRoutingLegacy)
                return false;
            foreach (var pair in _routingEdits)
            {
                if (!_routingOriginals.TryGetValue(pair.Key, out var original)
                    || !string.Equals(original, pair.Value.Fingerprint(), StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }

    private void NotifyRoutingChanged()
    {
        OnPropertyChanged(nameof(HasRoutingChanges));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    partial void OnSelectedRoutingTypeChanged(RoutingTypeItem? value)
    {
        RebuildRoutingGroups();
    }

    partial void OnSelectedPreferredJunctionChanged(RoutingJunctionOption? value)
    {
        if (value is null || SelectedRoutingType is null)
            return;
        var state = GetRoutingEditState(SelectedRoutingType);
        if (state.PreferredJunctionType != value.Value)
        {
            state.PreferredJunctionType = value.Value;
            NotifyRoutingChanged();
        }
    }

    private void InitializeRoutingTab(string? familySource, int? revitCategoryId)
    {
        IsRoutingTabVisible =
            string.Equals(familySource, "system", StringComparison.Ordinal)
            && RoutingGroupCatalog.IsMepCurveCategory(revitCategoryId)
            && _routingEditorService is not null;
        _routingHostCategoryId = revitCategoryId ?? 0;
        ShowPreferredJunction = IsRoutingTabVisible
            && RoutingGroupCatalog.HasPreferredJunction(_routingHostCategoryId);
        SmartConLogger.Info(
            $"InitializeRoutingTab (ADR-072 F3): familySource='{familySource ?? "<null>"}', " +
            $"revitCategoryId={revitCategoryId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<null>"}, " +
            $"service={_routingEditorService is not null}, visible={IsRoutingTabVisible}");
    }

    private async Task LoadRoutingAsync(CancellationToken ct)
    {
        if (!IsRoutingTabVisible || _routingEditorService is null)
            return;

        IsRoutingBusy = true;
        try
        {
            _routingData = await _routingEditorService.LoadAsync(_catalogItemId, ct).ConfigureAwait(true);
            if (_routingData is null)
            {
                IsRoutingTabVisible = false;
                return;
            }

            IsRoutingLegacy = _routingData.IsLegacyVersion;
            OnPropertyChanged(nameof(ShowRoutingEditor));
            OnPropertyChanged(nameof(CanEditRouting));
            OnPropertyChanged(nameof(RoutingLockHint));
            if (_routingData.HasTypeNameCollisions && !IsRoutingLegacy)
            {
                // Same-named types of different system families (duct
                // Round/Rect/Oval) cannot be recomposed byte-exactly from
                // the name-keyed V33 section storage — show read-only with
                // the hint, saves are refused by the service too.
                IsRoutingLegacy = true;
                OnPropertyChanged(nameof(ShowRoutingEditor));
                OnPropertyChanged(nameof(CanEditRouting));
            }

            _routingEdits.Clear();
            _routingOriginals.Clear();
            _routingSizeOptions = BuildSizeOptions(_routingData.SizeNominalsFeet);
            var multiFamily = _routingData.Types
                .Select(t => t.FamilyName)
                .Distinct(StringComparer.Ordinal)
                .Count() > 1;
            RoutingTypes = new ObservableCollection<RoutingTypeItem>(
                _routingData.Types.Select(t => new RoutingTypeItem(t, multiFamily)));
            foreach (var type in _routingData.Types)
            {
                var state = BuildRoutingEditState(type);
                _routingEdits[RoutingTypeItem.KeyOf(type.TypeName, type.FamilyKey)] = state;
                _routingOriginals[RoutingTypeItem.KeyOf(type.TypeName, type.FamilyKey)] = state.Fingerprint();
            }
            SelectedRoutingType = RoutingTypes.FirstOrDefault();
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"LoadRoutingAsync failed: {ex.Message} [Action: закройте и откройте свойства снова; вкладка трассировки будет скрыта]");
            IsRoutingTabVisible = false;
        }
        finally
        {
            IsRoutingBusy = false;
        }
    }

    private RoutingTypeEditState BuildRoutingEditState(RoutingEditorTypeData type)
    {
        var descriptors = RoutingGroupCatalog.GetGroups(_routingHostCategoryId, type.WithFittings);
        var missing = new HashSet<string>(_routingData!.MissingPartFamilies, StringComparer.Ordinal);
        var state = new RoutingTypeEditState(
            _routingData.Settings
                .FirstOrDefault(s => s.TypeName == type.TypeName && s.FamilyKey == type.FamilyKey)
                ?.PreferredJunctionType ?? 0);

        foreach (var descriptor in descriptors)
        {
            var group = new RoutingGroupEditState(descriptor);
            var storedRules = _routingData.Rules
                .Where(r => r.TypeName == type.TypeName
                    && r.FamilyKey == type.FamilyKey
                    && r.GroupKey == descriptor.GroupKey)
                .OrderBy(r => r.RuleOrder);
            foreach (var rule in storedRules)
            {
                group.Rules.Add(RoutingRuleEditState.FromStored(rule, missing));
            }
            // Param groups hold exactly one value row («Нет» when unset).
            if (!descriptor.AllowMultipleRules && !descriptor.IsReadOnly && group.Rules.Count == 0)
            {
                group.Rules.Add(RoutingRuleEditState.Empty());
            }
            state.Groups.Add(group);
        }
        return state;
    }

    private RoutingTypeEditState GetRoutingEditState(RoutingTypeItem type)
        => _routingEdits[RoutingTypeItem.KeyOf(type.TypeName, type.FamilyKey)];

    /// <summary>«Все» + the stored segment nominal diameters in mm, ascending.</summary>
    private static IReadOnlyList<string> BuildSizeOptions(IReadOnlyList<double> nominalsFeet)
    {
        var options = new List<string>(nominalsFeet.Count + 1)
        {
            LanguageManager.GetString(StringLocalization.Keys.FM_Routing_AllSizes) ?? "All",
        };
        options.AddRange(nominalsFeet.Select(f => RoutingTypeEditState.FormatSize(f, isMax: false)));
        return options;
    }

    private void RebuildRoutingGroups()
    {
        if (SelectedRoutingType is null)
        {
            RoutingGroups = [];
            return;
        }
        var state = GetRoutingEditState(SelectedRoutingType);
        RoutingGroups = new ObservableCollection<RoutingGroupRowViewModel>(
            state.Groups.Select(g => new RoutingGroupRowViewModel(this, g, _routingSizeOptions)));
        SelectedPreferredJunction = PreferredJunctionOptions
            .FirstOrDefault(o => o.Value == state.PreferredJunctionType);
    }

    [RelayCommand]
    private void AddRoutingRule(RoutingGroupRowViewModel? group)
    {
        if (group is null || IsRoutingReadOnly)
            return;
        group.State.Rules.Add(RoutingRuleEditState.Empty());
        group.RefreshRules();
        NotifyRoutingChanged();
    }

    [RelayCommand]
    private void RemoveRoutingRule(RoutingRuleRowViewModel? row)
    {
        if (row?.Group is null || IsRoutingReadOnly)
            return;
        row.Group.State.Rules.Remove(row.State);
        row.Group.RefreshRules();
        NotifyRoutingChanged();
    }

    [RelayCommand]
    private void MoveRoutingRuleUp(RoutingRuleRowViewModel? row) => MoveRoutingRule(row, -1);

    [RelayCommand]
    private void MoveRoutingRuleDown(RoutingRuleRowViewModel? row) => MoveRoutingRule(row, +1);

    private void MoveRoutingRule(RoutingRuleRowViewModel? row, int delta)
    {
        if (row?.Group is null || IsRoutingReadOnly)
            return;
        var rules = row.Group.State.Rules;
        var index = rules.IndexOf(row.State);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= rules.Count)
            return;
        (rules[index], rules[target]) = (rules[target], rules[index]);
        row.Group.RefreshRules();
        NotifyRoutingChanged();
    }

    [RelayCommand]
    private async Task PickRoutingPartAsync(RoutingRuleRowViewModel? row)
    {
        if (row?.Group is null || IsRoutingReadOnly || _routingEditorService is null)
            return;
        var descriptor = row.Group.State.Descriptor;
        using var _scope = SmartConLogger.BeginScope("RoutingEditor",
            ("Method", nameof(PickRoutingPartAsync)),
            ("Group", descriptor.GroupKey),
            ("FittingCategory", descriptor.FittingCategoryId));
        try
        {
            var pickerVm = _viewModelFactory.CreateRoutingPartPickerViewModel(
                descriptor.FittingCategoryId, descriptor.PartTypeOrdinals, row.State.PartName);
            await pickerVm.InitializeAsync();
            if (_dialogService.ShowRoutingPartPicker(pickerVm) == true && pickerVm.Result is { } partName)
            {
                row.State.PartName = partName;
                row.State.HasPresenceIssue = false;
                row.Refresh();
                NotifyRoutingChanged();
                SmartConLogger.Info($"Part picked: '{partName}'");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"PickRoutingPartAsync FAILED: {ex.GetType().Name}: {ex.Message}");
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_Tab_Routing) ?? "Routing",
                ex.Message);
        }
    }

    [RelayCommand]
    private void ClearRoutingPart(RoutingRuleRowViewModel? row)
    {
        if (row?.Group is null || IsRoutingReadOnly)
            return;
        row.State.PartName = null;
        row.State.HasPresenceIssue = false;
        row.Refresh();
        NotifyRoutingChanged();
    }

    internal void NotifyRoutingRuleEdited() => NotifyRoutingChanged();

    private bool IsRoutingReadOnly => IsReadOnly || IsRoutingLegacy;

    /// <summary>
    /// Saves the routing edits of every touched type as a new catalog
    /// version. Called from the main <c>SaveAsync</c> BEFORE the metadata
    /// save; a failure aborts the whole save (the error is already shown).
    /// </summary>
    private async Task<bool> SaveRoutingAsync()
    {
        if (_routingEditorService is null)
            return false;

        var editedTypes = new List<RoutingEditorTypeSave>();
        foreach (var type in _routingData!.Types)
        {
            var key = RoutingTypeItem.KeyOf(type.TypeName, type.FamilyKey);
            var state = _routingEdits[key];
            if (string.Equals(_routingOriginals[key], state.Fingerprint(), StringComparison.Ordinal))
                continue;
            if (!state.TryToRecords(type, out var records, out var invalidGroup))
            {
                _dialogService.ShowError(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Tab_Routing) ?? "Routing",
                    string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_Routing_InvalidSize) ?? "{0} ({1})",
                        invalidGroup, type.TypeName));
                return false;
            }
            editedTypes.Add(new RoutingEditorTypeSave(
                type.TypeName, type.FamilyKey, state.PreferredJunctionType, records));
        }

        if (editedTypes.Count == 0)
            return true;

        var result = await _routingEditorService
            .SaveAsync(_catalogItemId, new RoutingEditorSave(editedTypes))
            .ConfigureAwait(true);
        if (!result.Success)
        {
            var message = result.ErrorMessage switch
            {
                "LegacyVersion" => LanguageManager.GetString(
                    StringLocalization.Keys.FM_Routing_LegacyVersion) ?? result.ErrorMessage,
                "TypeNameCollisions" => LanguageManager.GetString(
                    StringLocalization.Keys.FM_Routing_DuplicateTypes) ?? result.ErrorMessage,
                _ => string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Routing_SaveFailed) ?? "{0}",
                    result.ErrorMessage),
            };
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_Tab_Routing) ?? "Routing", message);
            return false;
        }

        VersionsChanged = true;
        VersionLabel = result.NewVersionLabel;

        var status = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_Routing_Saved) ?? "{0}",
            result.NewVersionLabel);
        if (result.ArchivedLockedParts.Count > 0)
        {
            status += Environment.NewLine + string.Join(
                Environment.NewLine,
                result.ArchivedLockedParts.Select(part => string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Routing_ArchivedLockHint) ?? "{0}",
                    part)));
        }
        RoutingStatusMessage = status;

        // Reload from the DB so the editor state matches the new version
        // (and the dirty baseline resets to what was actually persisted).
        await LoadRoutingAsync(default).ConfigureAwait(true);
        await LoadVersionsAsync(default).ConfigureAwait(true);
        return true;
    }
}

/// <summary>Type selector row of the routing tab.</summary>
public sealed class RoutingTypeItem
{
    public RoutingTypeItem(RoutingEditorTypeData type, bool multiFamily)
    {
        TypeName = type.TypeName;
        FamilyKey = type.FamilyKey;
        FamilyName = type.FamilyName;
        WithFittings = type.WithFittings;
        DisplayName = multiFamily && FamilyName.Length > 0
            ? TypeName + "  (" + FamilyName + ")"
            : TypeName;
    }

    public string TypeName { get; }
    public string FamilyKey { get; }
    public string FamilyName { get; }
    public bool WithFittings { get; }
    public string DisplayName { get; }
    public override string ToString() => DisplayName;

    public static string KeyOf(string typeName, string familyKey) => familyKey + "|" + typeName;
}

/// <summary>Preferred-junction combo row (Tee=0 / Tap=1 — frozen enum ordinals).</summary>
public sealed record RoutingJunctionOption(int Value, string Label)
{
    public override string ToString() => Label;
}

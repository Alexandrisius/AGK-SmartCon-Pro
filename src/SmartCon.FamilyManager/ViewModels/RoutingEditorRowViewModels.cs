using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Editable per-type routing state (ADR-072, Phase 3): the preferred
/// junction scalar plus every group's rule rows in rule order. Text inputs
/// (min/max in millimetres) are kept as text so the dirty fingerprint and
/// the save-time parse see exactly what the user typed; non-primary
/// criteria ride through untouched (roundtrip preservation).
/// </summary>
public sealed class RoutingTypeEditState
{
    public RoutingTypeEditState(int preferredJunctionType)
    {
        PreferredJunctionType = preferredJunctionType;
    }

    public int PreferredJunctionType { get; set; }
    public List<RoutingGroupEditState> Groups { get; } = [];

    /// <summary>
    /// Order-sensitive canonical fingerprint of the current edit state —
    /// compared against the stored-rules fingerprint for dirty detection
    /// (rule order IS routing content).
    /// </summary>
    public string Fingerprint()
    {
        var sb = new System.Text.StringBuilder(128);
        sb.Append(PreferredJunctionType).Append('#');
        foreach (var group in Groups)
        {
            sb.Append(group.Descriptor.GroupKey).Append('@');
            foreach (var rule in group.Rules)
            {
                sb.Append(rule.PartName).Append(';')
                    .Append(rule.Description).Append(';')
                    .Append(rule.MinSizeText).Append(';')
                    .Append(rule.MaxSizeText).Append(';');
                foreach (var criterion in rule.OtherCriteria)
                {
                    sb.Append(criterion.CriterionType).Append(',')
                        .Append(criterion.MinimumSize.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
                        .Append(criterion.MaximumSize.ToString("0.######", CultureInfo.InvariantCulture)).Append(',');
                }
                sb.Append('/');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Edit state → storage records of one type. READ-ONLY groups (the pipe
    /// Segments row, FHV21) are SKIPPED — they are display-only views of
    /// per-version mini content and never persist through the editor (the
    /// service preserves any legacy stored rows verbatim instead; the old
    /// read-only path silently DROPPED the stored PrimarySizeCriterion).
    /// Synthesized empty rows of param groups persist as explicit no-part
    /// rules — semantically identical to «Нет» in Revit.
    /// </summary>
    public bool TryToRecords(
        RoutingEditorTypeData type,
        out IReadOnlyList<FamilyRoutingRuleInfo> records,
        out string? validationError)
    {
        var result = new List<FamilyRoutingRuleInfo>();
        validationError = null;
        foreach (var group in Groups)
        {
            if (group.Descriptor.IsReadOnly)
                continue;
            var order = 0;
            foreach (var rule in group.Rules)
            {
                // Param groups keep exactly one row — even a «Нет» one;
                // multi-rule groups skip blank rows and unpicked fresh rows.
                if (group.Descriptor.AllowMultipleRules
                    && (rule.IsEmpty || rule is { IsFresh: true, PartName: null }))
                    continue;
                if (!TryParseSize(rule.MinSizeText, isMax: false, out var min)
                    || !TryParseSize(rule.MaxSizeText, isMax: true, out var max))
                {
                    records = [];
                    validationError = group.Descriptor.GroupKey;
                    return false;
                }
                // A parsed min above max is never a valid criterion
                // (free-text input without segment sizes) — reject instead
                // of writing a never-matching rule (audit L18).
                if (group.Descriptor.HasCriteria && min > max)
                {
                    records = [];
                    validationError = group.Descriptor.GroupKey;
                    return false;
                }

                var criteria = new List<RoutingCriterionSnapshot>();
                if (group.Descriptor.HasCriteria)
                {
                    criteria.Add(new RoutingCriterionSnapshot("PrimarySizeCriterion", min, max));
                }
                criteria.AddRange(rule.OtherCriteria);

                result.Add(new FamilyRoutingRuleInfo(
                    type.TypeName,
                    type.FamilyKey,
                    group.Descriptor.GroupKey,
                    order++,
                    rule.PartName,
                    // Stored as-is (extraction never trims) — trimming here
                    // would shift the catalog fingerprint and surface a
                    // phantom RoutingDrift after any save (audit L19).
                    rule.Description,
                    criteria));
            }
        }
        records = result;
        return true;
    }

    /// <summary>
    /// The «all sizes» sentinel of the Revit routing dialog: stored rules
    /// carry 0..10000 ft for an unrestricted PrimarySizeCriterion — the
    /// editor shows it as «Все» and writes the same sentinel back, so an
    /// untouched rule roundtrips byte-exact.
    /// </summary>
    private const double AllSizesMaxFeet = 10000.0;
    private const double AllSizesThresholdFeet = 9999.0;

    private static string AllSizesLabel =>
        LanguageManager.GetString(StringLocalization.Keys.FM_Routing_AllSizes) ?? "All";

    internal static string AllSizesDisplay => AllSizesLabel;

    /// <summary>feet storage → display text (unbounded = «Все» in BOTH
    /// columns: an unrestricted bound reads as «all sizes», owner UI
    /// review 2026-08-30 — an empty min next to «Все» looked broken).</summary>
    internal static string FormatSize(double feet, bool isMax)
    {
        if (feet <= 0)
            return AllSizesLabel;
        if (isMax && feet >= AllSizesThresholdFeet)
            return AllSizesLabel;
        return (feet * 304.8).ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>display text → feet storage («Все»/empty = unbounded sentinel).</summary>
    private static bool TryParseSize(string text, bool isMax, out double feet)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0
            || string.Equals(trimmed, AllSizesLabel, StringComparison.OrdinalIgnoreCase))
        {
            feet = isMax ? AllSizesMaxFeet : 0.0;
            return true;
        }
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var mm)
            && !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out mm))
        {
            feet = 0;
            return false;
        }
        if (mm < 0)
        {
            feet = 0;
            return false;
        }
        feet = mm / 304.8;
        return true;
    }
}

/// <summary>One group's edit state (descriptor + ordered rule rows).</summary>
public sealed class RoutingGroupEditState
{
    public RoutingGroupEditState(RoutingGroupDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public RoutingGroupDescriptor Descriptor { get; }
    public List<RoutingRuleEditState> Rules { get; } = [];
}

/// <summary>One rule row's edit state.</summary>
public sealed class RoutingRuleEditState
{
    public string? PartName { get; set; }
    public string Description { get; set; } = string.Empty;
    public string MinSizeText { get; set; } = string.Empty;
    public string MaxSizeText { get; set; } = string.Empty;
    public List<RoutingCriterionSnapshot> OtherCriteria { get; } = [];
    public bool HasPresenceIssue { get; set; }

    /// <summary>Part-type ordinal of the picked part family (display-only
    /// metadata for the junctions grey-out; not part of the fingerprint).</summary>
    public int? PartTypeOrdinal { get; set; }

    public bool IsEmpty
        => PartName is null
            && Description.Trim().Length == 0
            && MinSizeText.Trim().Length == 0
            && MaxSizeText.Trim().Length == 0;

    /// <summary>
    /// A row added via «+» that the user has not picked a part for — skipped
    /// on save (audit M15: a default «Все» size pair made <see cref="IsEmpty"/>
    /// false, so an unpicked fresh row persisted as a ghost no-part rule in a
    /// multi-rule group — a state the Revit UI never produces). Stored no-part
    /// rules (FromStored) are NOT fresh and round-trip as before.
    /// </summary>
    public bool IsFresh { get; init; }

    /// <summary>
    /// A fresh rule row: no part («Нет»), max = «Все» (the Revit default of
    /// an unrestricted PrimarySizeCriterion), min unbounded.
    /// </summary>
    public static RoutingRuleEditState Empty(bool isFresh = false) => new()
    {
        IsFresh = isFresh,
        MinSizeText = RoutingTypeEditState.FormatSize(0, isMax: false),
        MaxSizeText = RoutingTypeEditState.FormatSize(0, isMax: true),
    };

    public static RoutingRuleEditState FromStored(
        FamilyRoutingRuleInfo rule, IReadOnlyCollection<string> missingPartFamilies)
    {
        var primary = rule.Criteria
            .FirstOrDefault(c => c.CriterionType == "PrimarySizeCriterion");
        var state = new RoutingRuleEditState
        {
            PartName = rule.PartName,
            Description = rule.Description,
            MinSizeText = RoutingTypeEditState.FormatSize(primary?.MinimumSize ?? 0, isMax: false),
            MaxSizeText = RoutingTypeEditState.FormatSize(primary?.MaximumSize ?? 0, isMax: true),
            HasPresenceIssue = rule.PartName is not null
                && rule.PartName.IndexOf(':') is var separator
                && separator > 0
                && missingPartFamilies.Contains(rule.PartName.Substring(0, separator)),
        };
        state.OtherCriteria.AddRange(
            rule.Criteria.Where(c => c.CriterionType != "PrimarySizeCriterion"));
        return state;
    }
}

/// <summary>UI row of one routing group (header + rule rows).</summary>
public sealed partial class RoutingGroupRowViewModel : ObservableObject
{
    private readonly FamilyPropertiesViewModel _owner;
    private int _preferredJunctionType;

    public RoutingGroupRowViewModel(
        FamilyPropertiesViewModel owner, RoutingGroupEditState state,
        IReadOnlyList<string> sizeOptions, int preferredJunctionType)
    {
        _owner = owner;
        State = state;
        SizeOptions = sizeOptions;
        _preferredJunctionType = preferredJunctionType;
        _label = ComputeLabel();
        RefreshRules();
    }

    public RoutingGroupEditState State { get; }

    [ObservableProperty] private string _label;

    /// <summary>The tee/tap-dependent manager group: its label follows the
    /// preferred junction type and its rules of the non-preferred part type
    /// render greyed out (Revit routing dialog behavior).</summary>
    public bool IsJunctionsGroup => RoutingGroupCatalog.IsJunctionsManagerGroup(State.Descriptor.ManagerGroupType);

    /// <summary>Current preferred junction type of the edited type (0 = tee, 1 = tap).</summary>
    public int PreferredJunctionType => _preferredJunctionType;

    /// <summary>Nominal-diameter dropdown source («Все» + segment sizes, mm, ascending).</summary>
    public IReadOnlyList<string> SizeOptions { get; }

    /// <summary>Smallest nominal size option (index 1 — index 0 is «Все»), or null without sizes.</summary>
    public string? SmallestSizeOption => SizeOptions.Count > 1 ? SizeOptions[1] : null;

    /// <summary>Largest nominal size option, or null without sizes.</summary>
    public string? LargestSizeOption => SizeOptions.Count > 1 ? SizeOptions[SizeOptions.Count - 1] : null;

    public bool IsReadOnly => State.Descriptor.IsReadOnly;
    public bool AllowMultipleRules => State.Descriptor.AllowMultipleRules;
    public bool HasCriteria => State.Descriptor.HasCriteria;
    /// <summary>Param-driven groups (flex/conduit/cable-tray) — the
    /// discriminator is the null manager type, not the rule-count flags:
    /// the pipe Segments row is also single-rule but is a MANAGER group
    /// (owner stress test 2026-09-01 — it was misclassified as param and
    /// gained a synthetic «Нет» row + clear-part button).</summary>
    public bool IsParamGroup => State.Descriptor.ManagerGroupType is null;
    public bool HasSizeOptions => State.Descriptor.HasCriteria && SizeOptions.Count > 1;

    /// <summary>Part picker is offered only for groups whose part set the
    /// editor owns — hidden on read-only rows and on the Segments row (the
    /// segment set is mini-project content, owner stress test 2026-09-01).</summary>
    public bool CanPickPart => !State.Descriptor.IsReadOnly && !State.Descriptor.IsSegmentRow;

    [ObservableProperty] private ObservableCollection<RoutingRuleRowViewModel> _rules = [];

    /// <summary>Preferred junction changed: relabel the junctions group and
    /// re-grey its rules (preferred tee greys taps, preferred tap greys tees).</summary>
    public void RefreshJunctionState(int preferredJunctionType)
    {
        _preferredJunctionType = preferredJunctionType;
        Label = ComputeLabel();
        foreach (var rule in Rules)
            rule.RefreshInactive(this, preferredJunctionType);
    }

    private string ComputeLabel()
        => IsJunctionsGroup
            ? (LanguageManager.GetString(_preferredJunctionType == 1
                ? StringLocalization.Keys.FM_Routing_Group_JunctionsTaps
                : StringLocalization.Keys.FM_Routing_Group_JunctionsTees)
               ?? State.Descriptor.GroupKey)
            : LanguageManager.GetString(State.Descriptor.LabelKey) ?? State.Descriptor.GroupKey;

    public void RefreshRules()
    {
        Rules = new ObservableCollection<RoutingRuleRowViewModel>(
            State.Rules.Select(r => new RoutingRuleRowViewModel(_owner, this, r)));
        foreach (var rule in Rules)
            rule.RefreshInactive(this, _preferredJunctionType);
    }
}

/// <summary>UI row of one routing rule.</summary>
public sealed partial class RoutingRuleRowViewModel : ObservableObject
{
    private readonly FamilyPropertiesViewModel _owner;

    public RoutingRuleRowViewModel(
        FamilyPropertiesViewModel owner, RoutingGroupRowViewModel group, RoutingRuleEditState state)
    {
        _owner = owner;
        Group = group;
        State = state;
        _partDisplay = state.PartName
            ?? LanguageManager.GetString(StringLocalization.Keys.FM_Routing_NoPart) ?? "None";
        _description = state.Description;
        // Stored rules may carry one unbounded side next to a concrete
        // bound (0..150); the editor forbids that mix — the unbounded side
        // reads as the extreme available size instead of «Все». Normalize
        // BEFORE exposing the properties so no change-notification fires.
        var minText = state.MinSizeText;
        var maxText = state.MaxSizeText;
        if (IsAllSizes(minText) && !IsAllSizes(maxText) && group.SmallestSizeOption is { } minOption)
        {
            minText = minOption;
            state.MinSizeText = minOption;
        }

        if (IsAllSizes(maxText) && !IsAllSizes(minText) && group.LargestSizeOption is { } maxOption)
        {
            maxText = maxOption;
            state.MaxSizeText = maxOption;
        }

        _minSizeText = minText;
        _maxSizeText = maxText;
        _hasPresenceIssue = state.HasPresenceIssue;
    }

    public RoutingGroupRowViewModel Group { get; }
    public RoutingRuleEditState State { get; }

    /// <summary>Nominal-diameter dropdown source (from the group).</summary>
    public IReadOnlyList<string> SizeOptions => Group.SizeOptions;

    /// <summary>Size pickers render only for criteria groups WITH stored segment sizes.</summary>
    public bool HasSizeOptions => Group.HasSizeOptions;

    /// <summary>Free-text fallback: criteria group but the version has no stored sizes.</summary>
    public bool ShowFreeTextSizes => Group.HasCriteria && !Group.HasSizeOptions;

    /// <summary>
    /// Param groups (flex/conduit/cable-tray) hold exactly one value — the
    /// picked part can only be CLEARED back to «Нет», not row-deleted
    /// (owner stress test 2026-08-30: the row had no way to drop the pick).
    /// </summary>
    public bool ShowClearPart => Group.IsParamGroup && State.PartName is not null;

    /// <summary>
    /// The trailing action column (row-delete for multi-rule groups,
    /// clear-part for param rows) has content — when nothing is selected
    /// and the group is single-rule the column collapses so the picker
    /// does not float with an empty gap to its right (owner stress test
    /// 2026-09-01).
    /// </summary>
    public bool ShowTrailingAction => Group.AllowMultipleRules || ShowClearPart;

    /// <summary>
    /// Junctions group rule of the non-preferred part type (tee rule while
    /// the preferred junction is a tap, and vice versa) — shown greyed out
    /// and blocked from editing, exactly like the Revit routing dialog.
    /// </summary>
    [ObservableProperty] private bool _isInactive;

    /// <summary>Editing is allowed: dialog editable AND the rule is not greyed out.</summary>
    public bool CanEditRule => _owner.CanEditRouting && !IsInactive;

    public string InactiveTooltip =>
        LanguageManager.GetString(StringLocalization.Keys.FM_Routing_InactiveJunction) ?? string.Empty;

    partial void OnIsInactiveChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditRule));
    }

    [ObservableProperty] private string _partDisplay;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _minSizeText;
    [ObservableProperty] private string _maxSizeText;
    [ObservableProperty] private bool _hasPresenceIssue;

    public string PresenceIssueTooltip =>
        LanguageManager.GetString(StringLocalization.Keys.FM_Routing_PartNotInCatalog) ?? string.Empty;

    public string SizeWatermark =>
        LanguageManager.GetString(StringLocalization.Keys.FM_Routing_AllSizes) ?? "All";

    partial void OnDescriptionChanged(string value)
    {
        State.Description = value;
        _owner.NotifyRoutingRuleEdited();
    }

    partial void OnMinSizeTextChanged(string value)
    {
        State.MinSizeText = value;
        // «Все» is an all-or-nothing bound: picking it in either column
        // applies it to both; picking a concrete value in one column
        // replaces «Все» on the other with the extreme available size
        // (max for MAX, min for MIN) — owner UI review 2026-08-30.
        // Equal-value sets are no-ops in ObservableProperty, so the
        // cross-set cannot recurse. An EMPTY free-text field counts as
        // unbounded («Все») — TryParseSize reads it as the sentinel too.
        if (IsAllSizes(value) || string.IsNullOrWhiteSpace(value))
            MaxSizeText = RoutingTypeEditState.AllSizesDisplay;
        else if (IsAllSizes(MaxSizeText) && Group.LargestSizeOption is { } maxOption)
            MaxSizeText = maxOption;
        _owner.NotifyRoutingRuleEdited();
    }

    partial void OnMaxSizeTextChanged(string value)
    {
        State.MaxSizeText = value;
        if (IsAllSizes(value) || string.IsNullOrWhiteSpace(value))
            MinSizeText = RoutingTypeEditState.AllSizesDisplay;
        else if (IsAllSizes(MinSizeText) && Group.SmallestSizeOption is { } minOption)
            MinSizeText = minOption;
        _owner.NotifyRoutingRuleEdited();
    }

    private static bool IsAllSizes(string value) =>
        string.Equals((value ?? string.Empty).Trim(), RoutingTypeEditState.AllSizesDisplay,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-read the display fields after a part pick/clear.</summary>
    public void Refresh()
    {
        PartDisplay = State.PartName
            ?? LanguageManager.GetString(StringLocalization.Keys.FM_Routing_NoPart) ?? "None";
        HasPresenceIssue = State.HasPresenceIssue;
        OnPropertyChanged(nameof(ShowClearPart));
        OnPropertyChanged(nameof(ShowTrailingAction));
        RefreshInactive(Group, Group.PreferredJunctionType);
    }

    /// <summary>Recompute the junctions grey-out for the current preference.</summary>
    public void RefreshInactive(RoutingGroupRowViewModel group, int preferredJunctionType)
    {
        IsInactive = group.IsJunctionsGroup
            && State.PartName is not null
            && RoutingGroupCatalog.IsInactiveJunctionPart(State.PartTypeOrdinal, preferredJunctionType);
    }
}

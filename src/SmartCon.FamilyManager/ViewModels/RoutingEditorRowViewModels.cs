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
    /// Edit state → storage records of one type. The read-only Segments
    /// group's rows ride through unchanged (they mirror the item's physical
    /// segments — dropping them would corrupt the routing and the hash).
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
            var order = 0;
            foreach (var rule in group.Rules)
            {
                // Param groups keep exactly one row — even a «Нет» one.
                if (group.Descriptor.AllowMultipleRules && rule.IsEmpty)
                    continue;
                if (!TryParseSize(rule.MinSizeText, isMax: false, out var min)
                    || !TryParseSize(rule.MaxSizeText, isMax: true, out var max))
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
                    rule.Description.Trim(),
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

    /// <summary>feet storage → display text (unbounded = «Все»).</summary>
    internal static string FormatSize(double feet, bool isMax)
    {
        if (feet <= 0)
            return isMax ? AllSizesLabel : string.Empty;
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

    public bool IsEmpty
        => PartName is null
            && Description.Trim().Length == 0
            && MinSizeText.Trim().Length == 0
            && MaxSizeText.Trim().Length == 0;

    /// <summary>
    /// A fresh rule row: no part («Нет»), max = «Все» (the Revit default of
    /// an unrestricted PrimarySizeCriterion), min unbounded.
    /// </summary>
    public static RoutingRuleEditState Empty() => new()
    {
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

    public RoutingGroupRowViewModel(
        FamilyPropertiesViewModel owner, RoutingGroupEditState state, IReadOnlyList<string> sizeOptions)
    {
        _owner = owner;
        State = state;
        SizeOptions = sizeOptions;
        Label = LanguageManager.GetString(state.Descriptor.LabelKey) ?? state.Descriptor.GroupKey;
        RefreshRules();
    }

    public RoutingGroupEditState State { get; }
    public string Label { get; }

    /// <summary>Nominal-diameter dropdown source («Все» + segment sizes, mm).</summary>
    public IReadOnlyList<string> SizeOptions { get; }

    public bool IsReadOnly => State.Descriptor.IsReadOnly;
    public bool AllowMultipleRules => State.Descriptor.AllowMultipleRules;
    public bool HasCriteria => State.Descriptor.HasCriteria;
    public bool IsParamGroup => !State.Descriptor.AllowMultipleRules && !State.Descriptor.IsReadOnly;
    public bool HasSizeOptions => State.Descriptor.HasCriteria && SizeOptions.Count > 1;

    [ObservableProperty] private ObservableCollection<RoutingRuleRowViewModel> _rules = [];

    public void RefreshRules()
    {
        Rules = new ObservableCollection<RoutingRuleRowViewModel>(
            State.Rules.Select(r => new RoutingRuleRowViewModel(_owner, this, r)));
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
        _minSizeText = state.MinSizeText;
        _maxSizeText = state.MaxSizeText;
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
        _owner.NotifyRoutingRuleEdited();
    }

    partial void OnMaxSizeTextChanged(string value)
    {
        State.MaxSizeText = value;
        _owner.NotifyRoutingRuleEdited();
    }

    /// <summary>Re-read the display fields after a part pick/clear.</summary>
    public void Refresh()
    {
        PartDisplay = State.PartName
            ?? LanguageManager.GetString(StringLocalization.Keys.FM_Routing_NoPart) ?? "None";
        HasPresenceIssue = State.HasPresenceIssue;
        OnPropertyChanged(nameof(ShowClearPart));
    }
}

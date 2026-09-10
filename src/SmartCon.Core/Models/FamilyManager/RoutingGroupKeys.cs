namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// String group identity of routing rules (FHV19, ADR-072). Manager-based
/// groups (pipe/duct) are identified by the
/// <c>RoutingPreferenceRuleGroupType</c> ordinal; parameter-based groups
/// (flex/conduit/cable-tray — types without a RoutingPreferenceManager)
/// are identified by a <c>"Param:&lt;BuiltInParameter-name&gt;"</c> key.
/// The string form is the storage format of the
/// <c>family_routing_rules.group_key</c> column.
/// </summary>
public static class RoutingGroupKeys
{
    /// <summary>Prefix of a parameter-based group key.</summary>
    public const string ParamPrefix = "Param:";

    /// <summary>
    /// <see cref="RoutingRuleSnapshot.GroupType"/> value of parameter-based
    /// groups (never a valid <c>RoutingPreferenceRuleGroupType</c> ordinal).
    /// </summary>
    public const int ParamGroupType = -1;

    /// <summary>Build the group key of a parameter-based routing group.</summary>
    public static string ForParam(string builtInParameterName) => ParamPrefix + builtInParameterName;

    /// <summary><c>true</c> when the key identifies a parameter-based group.</summary>
    public static bool IsParamGroup(string? groupKey)
        => groupKey is not null && groupKey.StartsWith(ParamPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The built-in parameter name of a parameter-based group key
    /// (<c>null</c> for manager-based keys).
    /// </summary>
    public static string? ParamNameOf(string? groupKey)
        => IsParamGroup(groupKey) ? groupKey![ParamPrefix.Length..] : null;

    // Manager-group names mirror RoutingPreferenceRuleGroupType ordinals
    // (Revit API enum — Core cannot reference it, I-09; the ordinals are
    // frozen API constants, probe-verified 2025: Segments=0 … Caps=10).
    private static readonly IReadOnlyDictionary<int, string> ManagerGroupNames =
        new Dictionary<int, string>
        {
            [0] = "Segments",
            [1] = "Elbows",
            [2] = "Junctions",
            [3] = "Crosses",
            [4] = "Transitions",
            [5] = "Unions",
            [6] = "MechanicalJoints",
            [7] = "TransitionsRectangularToRound",
            [8] = "TransitionsRectangularToOval",
            [9] = "TransitionsOvalToRound",
            [10] = "Caps",
        };

    /// <summary>Storage key of a manager-based group (enum name).</summary>
    public static string ForManagerGroup(int groupType)
        => ManagerGroupNames.TryGetValue(groupType, out var name)
            ? name
            : "Group" + groupType.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The <c>RoutingPreferenceRuleGroupType</c> ordinal of a manager-based
    /// storage key (<c>null</c> for parameter-based keys and unknown names).
    /// The forward-compatible <c>"Group{n}"</c> fallback written by
    /// <see cref="ForManagerGroup"/> for ordinals unknown to this build (a
    /// future Revit group) round-trips the raw ordinal instead of degrading
    /// to Segments(0) (audit M3).
    /// </summary>
    public static int? ManagerGroupTypeOf(string? groupKey)
    {
        if (groupKey is null || IsParamGroup(groupKey))
            return null;
        foreach (var pair in ManagerGroupNames)
        {
            if (string.Equals(pair.Value, groupKey, StringComparison.Ordinal))
                return pair.Key;
        }
        if (groupKey.StartsWith("Group", StringComparison.Ordinal)
#pragma warning disable CA1846 // Substring over AsSpan — net48 lacks the span-based int.TryParse overload
            && int.TryParse(
                groupKey.Substring("Group".Length),
#pragma warning restore CA1846
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var ordinal))
        {
            return ordinal;
        }
        return null;
    }
}

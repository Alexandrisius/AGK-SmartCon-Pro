using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Parameter-based routing storage (FHV19, ADR-072, probe
/// <c>RoutingStorageReality</c> 2026-08-29): flex pipe/duct, conduit and
/// cable tray types have NO <c>RoutingPreferenceManager</c> (the property
/// is null) — their fitting selection lives in visible built-in type
/// parameters. Which parameters are VISIBLE (present in
/// <c>Element.Parameters</c>) is decided by Revit per type class — the
/// matrix matches the routing UI row-by-row (e.g. CableTray without
/// Fittings hides TEE/CROSS). Membership in <c>.Parameters</c> is the
/// authority; this set is only the candidate filter. On pipe/duct the
/// same built-ins are hidden (manager-backed), so the filter is a no-op
/// there.
/// </summary>
internal static class RoutingDrivingParameters
{
    /// <summary>
    /// ElementId routing parameters (fitting references). Extracted as
    /// one ROUTING rule each (<c>"Param:&lt;BIP&gt;"</c> group), excluded
    /// from VALUES.
    /// </summary>
    private static readonly HashSet<BuiltInParameter> FittingParams =
    [
        BuiltInParameter.RBS_CURVETYPE_DEFAULT_BEND_PARAM,
        BuiltInParameter.RBS_CURVETYPE_DEFAULT_CROSS_PARAM,
        BuiltInParameter.RBS_CURVETYPE_DEFAULT_ELBOW_PARAM,
        BuiltInParameter.RBS_CURVETYPE_DEFAULT_ELBOWDOWN_PARAM,
        BuiltInParameter.RBS_CURVETYPE_DEFAULT_ELBOWUP_PARAM,
        BuiltInParameter.RBS_CURVETYPE_DEFAULT_HORIZONTAL_BEND_PARAM,
        BuiltInParameter.RBS_CURVETYPE_DEFAULT_MECHJOINT_PARAM,
        BuiltInParameter.RBS_CURVETYPE_DEFAULT_TAKEOFF_PARAM,
        BuiltInParameter.RBS_CURVETYPE_DEFAULT_TEE_PARAM,
        BuiltInParameter.RBS_CURVETYPE_DEFAULT_TEEDOWN_PARAM,
        BuiltInParameter.RBS_CURVETYPE_DEFAULT_TEEUP_PARAM,
        BuiltInParameter.RBS_CURVETYPE_DEFAULT_TRANSITION_PARAM,
        BuiltInParameter.RBS_CURVETYPE_DEFAULT_UNION_PARAM,
        BuiltInParameter.RBS_CURVETYPE_MULTISHAPE_TRANSITION_PARAM,
        BuiltInParameter.RBS_CURVETYPE_MULTISHAPE_TRANSITION_RECTOVAL_PARAM,
        BuiltInParameter.RBS_CURVETYPE_MULTISHAPE_TRANSITION_OVALROUND_PARAM,
    ];

    /// <summary>
    /// The built-in parameter of a visible routing parameter, or
    /// <c>null</c> when the parameter is not routing-driving (shared /
    /// project / non-routing built-in).
    /// </summary>
    public static BuiltInParameter? TryGetRoutingParam(Parameter param)
    {
        if (param.Definition is not InternalDefinition internalDef)
            return null;
        var bip = internalDef.BuiltInParameter;
        return FittingParams.Contains(bip) ? bip : null;
    }

    /// <summary>
    /// <c>true</c> for the integer preferred-junction parameter (flex
    /// types) — extracted as <see cref="RoutingPreferencesSnapshot.PreferredJunctionType"/>,
    /// not as a rule.
    /// </summary>
    public static bool IsPreferredBranch(Parameter param)
        => param.Definition is InternalDefinition internalDef
            && internalDef.BuiltInParameter == BuiltInParameter.RBS_CURVETYPE_PREFERRED_BRANCH_PARAM;

    /// <summary>Group key of a parameter-based routing group.</summary>
    public static string GroupKey(BuiltInParameter bip) => RoutingGroupKeys.ForParam(bip.ToString());
}

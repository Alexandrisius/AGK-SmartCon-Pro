namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Behavior flags of a loadable family (ADR-056, Issue #159), read from
/// built-in parameters on the <c>Family</c> element (<c>OwnerFamily</c>) —
/// they are NOT family parameters and therefore invisible to
/// <c>FamilyManager.GetParameters()</c>. <c>null</c> = the parameter is
/// absent in this Revit version / family template (canonical marker
/// <c>-</c>; deterministic).
/// </summary>
/// <param name="IsShared"><c>FAMILY_SHARED</c> — family is shared
/// (nested instances become individually selectable).</param>
/// <param name="IsWorkPlaneBased"><c>FAMILY_WORK_PLANE_BASED</c> —
/// family requires a work plane when placed.</param>
/// <param name="IsAlwaysVertical"><c>FAMILY_ALWAYS_VERTICAL</c> —
/// family stays vertical regardless of host.</param>
/// <param name="AllowsCutWithVoids"><c>FAMILY_ALLOW_CUT_WITH_VOIDS</c> —
/// voids in this family cut the host when loaded.</param>
public sealed record FamilyBehaviorFlags(
    bool? IsShared,
    bool? IsWorkPlaneBased,
    bool? IsAlwaysVertical,
    bool? AllowsCutWithVoids);

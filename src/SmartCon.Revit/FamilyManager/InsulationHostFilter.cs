using Autodesk.Revit.DB;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Issue #181: shared insulation-host filter. Pipe/duct instances (including
/// flex) carrying insulation exist ONLY as hosts — the insulation type cannot
/// materialize without one (CF-4720) — so they are Revit API artifacts, not
/// standalone content. #197: the same applies to duct LINING hosts
/// (DuctLining : InsulationLiningBase — covered automatically).
/// Used by <c>SystemFamilyRevitOperations.AnalyzeActiveProject</c> (only for
/// SmartCon mini-projects — #181 scope correction 2026-08-06; the picker
/// never applies it) and <c>RevitFamilyMigrationExtractor</c> (staged
/// mini-project category detection — a host pipe must not make an insulation
/// mini-project detect as "Трубы").
/// </summary>
internal static class InsulationHostFilter
{
    /// <summary>Categories whose instances may exist only as insulation hosts.</summary>
    public static bool IsInsulationHostCategory(BuiltInCategory bic)
        => bic == BuiltInCategory.OST_PipeCurves
            || bic == BuiltInCategory.OST_DuctCurves
            || bic == BuiltInCategory.OST_FlexPipeCurves
            || bic == BuiltInCategory.OST_FlexDuctCurves;

    /// <summary>
    /// One collector pass over all insulation/lining elements → the set of
    /// their host element ids. Far cheaper than per-element
    /// <c>GetInsulationIds</c> on a large project (thousands of pipes).
    /// </summary>
    public static HashSet<ElementId> CollectInsulatedHostIds(Document doc)
    {
        var hostIds = new HashSet<ElementId>();
        foreach (var insulation in new FilteredElementCollector(doc)
            .OfClass(typeof(InsulationLiningBase))
            .WhereElementIsNotElementType()
            .Cast<InsulationLiningBase>())
        {
            var hostId = insulation.HostElementId;
            if (hostId is not null && hostId != ElementId.InvalidElementId)
            {
                hostIds.Add(hostId);
            }
        }
        return hostIds;
    }
}

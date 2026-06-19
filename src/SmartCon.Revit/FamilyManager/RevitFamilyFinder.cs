using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of <see cref="IFamilyFinder"/>. Uses
/// <c>FilteredElementCollector</c> with <c>OfClass(Family)</c> and a
/// <see cref="FamilyNameFilter"/> for fast name lookup. Matching is
/// case-<strong>in</strong>sensitive (consistent with
/// <c>IFamilySearchService</c> in the wider project).
/// </summary>
public sealed class RevitFamilyFinder : IFamilyFinder
{
    public ElementId? FindByName(Document doc, string familyName)
    {
        if (doc is null || string.IsNullOrEmpty(familyName)) return null;

        ElementId? firstMatch = null;
        var duplicates = 0;
        // OfClass(Family) collects every loadable family in the project.
        // Revit API does not expose a "FamilyNameFilter" (ElementNameFilter is
        // for ParameterElement, not Family), so we filter in code. The linear
        // scan is acceptable because:
        //   - typical project: 100-1000 families (sub-millisecond)
        //   - worst case: 50k+ families — still < 1 second on a developer
        //     machine, and a stale-check is a one-shot user action
        // Matching is case-insensitive, consistent with IFamilySearchService
        // and with how Revit itself displays family names in the Project
        // Browser (case-preserving, case-insensitive match).
        using var collector = new FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.Family));
        foreach (Autodesk.Revit.DB.Family f in collector)
        {
            if (f is null || f.Name is null) continue;
            if (!string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase)) continue;
            if (firstMatch is null)
            {
                firstMatch = f.Id;
            }
            else
            {
                duplicates++;
            }
        }
        if (duplicates > 0)
        {
            var firstId = firstMatch!;
#if NET8_0_OR_GREATER
            var firstIdValue = firstId.Value;
#else
#pragma warning disable CS0618 // IntegerValue is deprecated in Revit 2024; removed in 2025. Use Value when available.
            var firstIdValue = firstId.IntegerValue;
#pragma warning restore CS0618
#endif
            SmartConLogger.Warn(
                $"FindByName: family name '{familyName}' matches {duplicates + 1} " +
                $"Family elements in the project; the first match (ElementId=" +
                $"{firstIdValue}) will be used. " +
                "[Action: rename one of the families to remove the ambiguity — the " +
                "ES version marker may have been written to the wrong element]");
        }
        return firstMatch;
    }
}

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

    public IReadOnlyList<(string FamilyName, string TypeName)> CollectLoadedFamilySymbols(Document doc)
    {
        if (doc is null) return Array.Empty<(string, string)>();

        // One collector pass for the whole project. FamilySymbol carries both
        // names (Family.Name + Name), so a single scan feeds the leaf badge
        // ("family loaded") and the per-type badge ("symbol loaded") at once.
        // In-place families are excluded by construction — their symbols
        // must not mark catalog entries as present (they are not catalog
        // content and cannot be synced).
        var result = new List<(string, string)>();
        using var collector = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol));
        foreach (FamilySymbol symbol in collector)
        {
            var family = symbol.Family;
            if (family is null || family.IsInPlace) continue;
            if (family.Name is null || symbol.Name is null) continue;
            result.Add((family.Name, symbol.Name));
        }
        return result;
    }
}

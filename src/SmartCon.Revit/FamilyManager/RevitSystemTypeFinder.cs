using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of <see cref="ISystemTypeFinder"/> (Issue #104).
/// System types are matched by (name, category): equal type names in
/// different categories ("Стандартный" pipe vs wall) must never collide.
/// </summary>
public sealed class RevitSystemTypeFinder : ISystemTypeFinder
{
    public ElementId? FindTypeByName(Document doc, string typeName, int? categoryOrdinal)
    {
        if (doc is null || string.IsNullOrEmpty(typeName)) return null;

        ElementId? firstMatch = null;
        var duplicates = 0;
        using var collector = CreateCollector(doc, categoryOrdinal);
        foreach (var type in collector.Cast<ElementType>())
        {
            if (type.Name is null) continue;
            if (!string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase)) continue;
            if (firstMatch is null)
            {
                firstMatch = type.Id;
            }
            else
            {
                duplicates++;
            }
        }

        if (duplicates > 0)
        {
            SmartConLogger.Warn(
                $"FindTypeByName: type name '{typeName}' matches {duplicates + 1} " +
                $"ElementType elements (category filter: {(categoryOrdinal.HasValue ? categoryOrdinal.Value.ToString() : "<none>")}); " +
                "the first match will be used. " +
                "[Action: pass the category ordinal to disambiguate — the ES version " +
                "marker may have been written to the wrong element]");
        }
        return firstMatch;
    }

    public IReadOnlyList<SystemTypeLocation> CollectTypes(
        Document doc, IReadOnlyCollection<int> categoryOrdinals)
    {
        if (doc is null || categoryOrdinals is null || categoryOrdinals.Count == 0)
            return Array.Empty<SystemTypeLocation>();

        var result = new List<SystemTypeLocation>();
        foreach (var ordinal in categoryOrdinals.Distinct())
        {
            using var collector = CreateCollector(doc, ordinal);
            foreach (var type in collector.Cast<ElementType>())
            {
                if (type.Name is null) continue;
                result.Add(new SystemTypeLocation(type.Name, ordinal, type.Id));
            }
        }
        return result;
    }

    private static FilteredElementCollector CreateCollector(Document doc, int? categoryOrdinal)
    {
        var collector = new FilteredElementCollector(doc).OfClass(typeof(ElementType));
        if (categoryOrdinal.HasValue)
        {
            try
            {
                collector = collector.OfCategoryId(ElementIdCompat.Create(categoryOrdinal.Value));
            }
            catch (Exception ex)
            {
                // Unknown/custom category ordinal — fall back to the
                // unfiltered collector so the name match still works.
                SmartConLogger.Debug(
                    $"CreateCollector: category ordinal {categoryOrdinal.Value} rejected " +
                    $"({ex.Message}) — falling back to unfiltered collector.");
            }
        }
        return collector;
    }
}

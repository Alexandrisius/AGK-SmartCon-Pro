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
    public ElementId? FindTypeByName(
        Document doc, string typeName, int? categoryOrdinal, string? familyName = null, string? familyKey = null)
    {
        if (doc is null || string.IsNullOrEmpty(typeName)) return null;

        ElementId? firstMatch = null;
        var duplicates = 0;
        using var collector = CreateCollector(doc, categoryOrdinal);
        foreach (var type in collector.Cast<ElementType>())
        {
            if (type.Name is null) continue;
            // Manual test 2026-08-04: the electrical settings graph
            // (WireMaterialType and friends) shares the name, FamilyName
            // ("Провода") and category with the real WireType — a plain
            // ElementType match can bind a settings object instead of the
            // wire type (9-11 same-name "matches" seen in the wild), and the
            // whole sync then targets the wrong element. Settings objects
            // are never sync/import targets — excluded from matching.
            if (IsElectricalSettingsObject(type)) continue;
            if (!string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase)) continue;
            // #190 (ADR-064): the locale-invariant key is the PRIMARY family
            // filter — the localized FamilyName never matches across locales.
            // #183 (legacy): when no key is supplied, the family name
            // restricts the match — "Стандарт" of "Conduit with Fittings"
            // must never match "Conduit without Fittings".
            if (!string.IsNullOrEmpty(familyKey))
            {
                if (!string.Equals(SystemFamilyKeyResolver.Resolve(type), familyKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            else if (!string.IsNullOrEmpty(familyName)
                && !string.Equals(type.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
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
                $"ElementType elements (category filter: {(categoryOrdinal.HasValue ? categoryOrdinal.Value.ToString() : "<none>")}, " +
                $"family filter: {familyName ?? "<none>"}); " +
                "the first match will be used. " +
                "[Action: pass the family name and category ordinal to disambiguate — the ES version " +
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
                if (IsElectricalSettingsObject(type)) continue;
                // #183: FamilyName collected so stale detection can match
                // by (family, name) instead of name alone.
                // #190 (ADR-064): FamilyKey is the locale-invariant identity
                // — always computable for system types.
                result.Add(new SystemTypeLocation(
                    type.Name, ordinal, type.Id, type.FamilyName, SystemFamilyKeyResolver.Resolve(type)));
            }
        }
        return result;
    }

    /// <summary>
    /// Settings-graph object types (electrical) that surface in ElementType
    /// collectors with the same name/family/category as the real wire type
    /// but are never legitimate sync or import targets (manual test
    /// 2026-08-04). All three inherit ElementType (since the 2011 API);
    /// WireConduitType is not Element-derived and never reaches a
    /// collector — not listed. Revit 2026 replaced WireMaterialType/
    /// TemperatureRatingType/InsulationType with the Conductor* model —
    /// those are plain data objects (not Element-derived), so they never
    /// reach a collector and are not listed either.
    /// </summary>
    internal static bool IsElectricalSettingsObject(ElementType type) => type is
#if !REVIT2026_OR_GREATER
        Autodesk.Revit.DB.Electrical.WireMaterialType or
        Autodesk.Revit.DB.Electrical.TemperatureRatingType or
        Autodesk.Revit.DB.Electrical.InsulationType or
#endif
        Autodesk.Revit.DB.Electrical.VoltageType or
        Autodesk.Revit.DB.Electrical.DistributionSysType;

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

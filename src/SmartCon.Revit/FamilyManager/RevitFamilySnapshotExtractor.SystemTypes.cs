using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class RevitFamilySnapshotExtractor
{
    public SystemFamilySnapshot ExtractFromProject(
        Document projectDoc,
        IReadOnlyList<string> typeUniqueIds,
        BuiltInCategory builtInCategory)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(projectDoc);
        ArgumentNullException.ThrowIfNull(typeUniqueIds);
#else
        if (projectDoc is null) throw new ArgumentNullException(nameof(projectDoc));
        if (typeUniqueIds is null) throw new ArgumentNullException(nameof(typeUniqueIds));
#endif

        using var _scope = SmartConLogger.BeginScope("SnapshotExtract",
            ("Method", nameof(ExtractFromProject)),
            ("Category", builtInCategory.ToString()),
            ("TypeCount", typeUniqueIds.Count));

        var types = new List<SystemTypeSnapshot>();
        string categoryName = string.Empty;

        foreach (var uniqueId in typeUniqueIds)
        {
            var element = projectDoc.GetElement(uniqueId);
            if (element is not ElementType elementType)
            {
                SmartConLogger.Warn(
                    $"Element '{uniqueId}' is not an ElementType — skipped. " +
                    "[Action: verify the UniqueId points to a system family type]");
                continue;
            }

            if (string.IsNullOrEmpty(categoryName))
            {
                categoryName = elementType.Category?.Name ?? builtInCategory.ToString();
            }

            var typeSnapshot = ExtractSystemType(elementType, projectDoc);
            types.Add(typeSnapshot);
        }

        var sortedTypes = types
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var categoryId = (int)builtInCategory;

        var paramSummary = sortedTypes.Count > 0
            ? string.Join(", ", sortedTypes[0].Values.Take(10).Select(v => $"{v.ParameterName}={(v.HasValue ? v.ValueText ?? v.ValueNumber?.ToString() ?? "?" : "EMPTY")}"))
            : "<no types>";
        SmartConLogger.Info(
            $"System snapshot: '{categoryName}', {sortedTypes.Count} types, " +
            $"{(sortedTypes.Count > 0 ? sortedTypes[0].Values.Count : 0)} params. " +
            $"First type '{sortedTypes.FirstOrDefault()?.Name}': {paramSummary}");

        return new SystemFamilySnapshot(
            CategoryName: categoryName,
            CategoryId: categoryId,
            Types: sortedTypes);
    }

    /// <summary>
    /// BuiltInCategory ordinal of the family category, or <c>null</c> when
    /// the category is unavailable. ADR-055 (family facts).
    /// </summary>
    private static int? GetCategoryOrdinal(Category? familyCategory)
    {
        var id = familyCategory?.Id;
        if (id is null) return null;
#if REVIT2024_OR_GREATER
        return (int)id.Value;
#else
        return id.IntegerValue;
#endif
    }

    public SystemTypeSnapshot ExtractSingleSystemType(Document projectDoc, ElementId typeId)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(projectDoc);
        ArgumentNullException.ThrowIfNull(typeId);
#else
        if (projectDoc is null) throw new ArgumentNullException(nameof(projectDoc));
        if (typeId is null) throw new ArgumentNullException(nameof(typeId));
#endif

        var elementType = projectDoc.GetElement(typeId) as ElementType
            ?? throw new InvalidOperationException(
                $"Element {typeId} is not an ElementType in the given document.");

        return ExtractSystemType(elementType, projectDoc);
    }

    /// <summary>
    /// Lightweight routing-only read of one system type (ADR-072 World B):
    /// the routing drift probe (stale check / placement dialog) needs just
    /// the routing preferences — manager- or parameter-based — without the
    /// full parameter/structure extraction. <c>null</c> for non-MEP types.
    /// </summary>
    public RoutingPreferencesSnapshot? ExtractSystemTypeRouting(Document projectDoc, ElementId typeId)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(projectDoc);
        ArgumentNullException.ThrowIfNull(typeId);
#else
        if (projectDoc is null) throw new ArgumentNullException(nameof(projectDoc));
        if (typeId is null) throw new ArgumentNullException(nameof(typeId));
#endif

        return projectDoc.GetElement(typeId) is ElementType elementType
            ? ExtractRoutingPreferences(elementType, projectDoc)
            : null;
    }

    private static SystemTypeSnapshot ExtractSystemType(
        ElementType elementType, Document projectDoc)
    {
        var name = elementType.Name;

        var paramDict = new SortedDictionary<string, Parameter>(StringComparer.Ordinal);
        var routingDrivingCount = 0;
        foreach (Parameter param in elementType.Parameters)
        {
            var pname = param.Definition?.Name;
            if (string.IsNullOrEmpty(pname)) continue;
            // FHV19 (ADR-072): routing-driving parameters (fitting selection
            // of manager-less MEPCurve types — flex/conduit/cable-tray) leave
            // VALUES and become ROUTING rules. Their ElementId tokens
            // reference project fittings — the same phantom-diff class as
            // #254. On pipe/duct these built-ins are hidden from
            // Element.Parameters, so this filter is a no-op there.
            if (RoutingDrivingParameters.TryGetRoutingParam(param) is not null
                || RoutingDrivingParameters.IsPreferredBranch(param))
            {
                routingDrivingCount++;
                continue;
            }
            // Same-name duplicate definitions (a shared parameter plus an
            // invisible clone with a different GUID — owner stress test
            // 2026-08-30): Element.Parameters enumerates BOTH. Keep the one
            // with a value so the snapshot/hash sees the real data, not the
            // empty clone (enumeration order is not contractually stable).
            if (paramDict.TryGetValue(pname!, out var existing) && existing.HasValue && !param.HasValue)
                continue;
            paramDict[pname!] = param;
        }

        SmartConLogger.Debug(
            $"ExtractSystemType '{name}': {paramDict.Count} params from Element.Parameters " +
            $"({routingDrivingCount} routing-driving excluded to ROUTING): " +
            $"[{string.Join(", ", paramDict.Keys)}]");

        var values = new List<SystemParameterValue>(paramDict.Count);

        foreach (var pair in paramDict)
        {
            var paramName = pair.Key;
            var param = pair.Value;
            var storageType = param.StorageType.ToString();
            var hasValue = param.HasValue;

            if (!hasValue)
            {
                values.Add(new SystemParameterValue(
                    ParameterName: paramName!,
                    StorageType: storageType,
                    HasValue: false,
                    ValueText: null,
                    ValueNumber: null,
                    ResolvedElementName: null));
                continue;
            }

            try
            {
                string? valueText = null;
                double? valueNumber = null;
                string? resolvedName = null;
                string? valueDisplay = null;
                string? specTypeId = null;
                string? unitTypeId = null;

                switch (param.StorageType)
                {
                    case StorageType.Double:
                        var dblVal = param.AsDouble();
                        valueNumber = dblVal;
                        valueText = FormattableString.Invariant($"{dblVal:0.######}");
                        valueDisplay = Compatibility.RevitUnitsCompat.FormatDisplayValue(projectDoc, param, dblVal);
                        specTypeId = Compatibility.RevitUnitsCompat.GetSpecTypeIdString(param.Definition);
                        unitTypeId = Compatibility.RevitUnitsCompat.GetUnitTypeIdString(param);
                        break;

                    case StorageType.Integer:
                        var intVal = param.AsInteger();
                        valueNumber = intVal;
                        valueText = intVal.ToString();
                        break;

                    case StorageType.String:
                        var strVal = param.AsString();
                        valueText = strVal ?? string.Empty;
                        break;

                    case StorageType.ElementId:
                        var elemId = param.AsElementId();
                        if (elemId is not null && elemId != ElementId.InvalidElementId)
                        {
                            var elem = projectDoc.GetElement(elemId);
                            resolvedName = elem?.Name;
                            valueText = resolvedName ?? elemId.ToString();
                        }
                        else
                        {
                            valueText = "INVALID";
                        }
                        break;

                    default:
                        valueText = "UNSUPPORTED";
                        break;
                }

                values.Add(new SystemParameterValue(
                    ParameterName: paramName!,
                    StorageType: storageType,
                    HasValue: true,
                    ValueText: valueText,
                    ValueNumber: valueNumber,
                    ResolvedElementName: resolvedName,
                    ValueDisplay: valueDisplay,
                    SpecTypeId: specTypeId,
                    UnitTypeId: unitTypeId));
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug(
                    $"Failed to read system parameter '{paramName}': {ex.Message}");
                values.Add(new SystemParameterValue(
                    ParameterName: paramName!,
                    StorageType: storageType,
                    HasValue: true,
                    ValueText: "READERROR",
                    ValueNumber: null,
                    ResolvedElementName: null));
            }
        }

        var sortedValues = values
            .OrderBy(v => v.ParameterName, StringComparer.Ordinal)
            .ToList();

        var included = sortedValues.Where(v => v.HasValue && !IsBlankValue(v) && !IsAutoGeneratedName(v.ParameterName)).ToList();
        var excludedAutoGen = sortedValues.Where(v => IsAutoGeneratedName(v.ParameterName)).ToList();
        var excludedBlank = sortedValues.Where(v => !v.HasValue || IsBlankValue(v)).ToList();
        SmartConLogger.Debug(
            $"ExtractSystemType '{name}' hash-input: {included.Count} included, " +
            $"{excludedBlank.Count} excluded (empty/blank), " +
            $"{excludedAutoGen.Count} excluded (auto-generated: differs per document). " +
            $"INCLUDED: [{string.Join(", ", included.Select(v => $"{v.ParameterName}={FormatValuePreview(v)}"))}] " +
            $"EXCLUDED_AUTO_GEN: [{string.Join(", ", excludedAutoGen.Select(v => $"{v.ParameterName}={FormatValuePreview(v)}"))}] " +
            $"EXCLUDED_BLANK: [{string.Join(", ", excludedBlank.Select(v => $"{v.ParameterName}=({(v.HasValue ? "blank:" + FormatValuePreview(v) : "EMPTY")})"))}]");

        return new SystemTypeSnapshot(
            Name: name,
            Values: sortedValues,
            Structure: ExtractCompoundStructure(elementType, projectDoc),
            Routing: ExtractRoutingPreferences(elementType, projectDoc),
            // #183: the system family is the identity key of the sync
            // (FamilyName + Name, never name alone). Sync-only — NOT part
            // of the content hash (FHV4 candidate, #179).
            FamilyName: elementType.FamilyName,
            // #190 (ADR-064): locale-invariant family identity — the primary
            // matcher on mixed-locale teams. Sync-only, not hashed.
            FamilyKey: SystemFamilyKeyResolver.Resolve(elementType),
            // FHV4 (ADR-065): subtype/structure/segment identity summaries.
            Stairs: ExtractStairsSubtypes(elementType, projectDoc),
            Railing: ExtractRailingStructure(elementType, projectDoc),
            Segments: ExtractSegments(elementType, projectDoc),
            // FHV5: wire settings graph (material/rating/insulation/size/
            // conduit) — WireType properties, invisible to Element.Parameters.
            Wire: ExtractWireSettings(elementType));
    }

    /// <summary>
    /// Extract a <see cref="SystemFamilySnapshot"/> from a staged
    /// mini-project (.rvt) during database actualization (ADR-056).
    /// Type discovery mirrors the staging contract
    /// (<c>SystemFamilyRevitOperations.CreateCleanProjectWithTypesAndInstances</c>):
    /// placed instances are the domain truth (ADR-027 Phase 2 — all 14
    /// categories place instances). The "all category types" fallback serves
    /// only legacy staged files created BEFORE Phase 2 (copied without
    /// placement) — the caller (hash task) trims them to the catalog's
    /// authoritative type list from <c>family_types</c>.
    /// </summary>
    public SystemFamilySnapshot ExtractSystemCategoryFromStagedProject(
        Document stagedDoc, BuiltInCategory builtInCategory)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(stagedDoc);
#else
        if (stagedDoc is null) throw new ArgumentNullException(nameof(stagedDoc));
#endif

        using var _scope = SmartConLogger.BeginScope("SnapshotExtract",
            ("Method", nameof(ExtractSystemCategoryFromStagedProject)),
            ("Category", builtInCategory.ToString()));

        var typeIds = new List<ElementId>();
        var seenIds = new HashSet<long>();

        foreach (var instance in new FilteredElementCollector(stagedDoc)
            .OfCategory(builtInCategory)
            .WhereElementIsNotElementType())
        {
            var typeId = instance.GetTypeId();
            if (typeId is not null && typeId != ElementId.InvalidElementId && seenIds.Add(GetElementIdValue(typeId)))
            {
                typeIds.Add(typeId);
            }
        }

        var fromPlacedInstances = typeIds.Count > 0;
        if (!fromPlacedInstances)
        {
            foreach (var type in new FilteredElementCollector(stagedDoc)
                .OfCategory(builtInCategory)
                .WhereElementIsElementType())
            {
                if (seenIds.Add(GetElementIdValue(type.Id)))
                {
                    typeIds.Add(type.Id);
                }
            }
        }

        SmartConLogger.Info(
            $"Staged system extraction: {builtInCategory}, {typeIds.Count} types " +
            $"({(fromPlacedInstances ? "placed instances" : "all category types — trimmed by caller")})");

        var types = new List<SystemTypeSnapshot>(typeIds.Count);
        string categoryName = string.Empty;

        foreach (var typeId in typeIds)
        {
            if (stagedDoc.GetElement(typeId) is not ElementType elementType)
                continue;

            if (string.IsNullOrEmpty(categoryName))
            {
                categoryName = elementType.Category?.Name ?? builtInCategory.ToString();
            }

            types.Add(ExtractSystemType(elementType, stagedDoc));
        }

        var sortedTypes = types
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        return new SystemFamilySnapshot(
            CategoryName: categoryName,
            CategoryId: (int)builtInCategory,
            Types: sortedTypes);
    }
}

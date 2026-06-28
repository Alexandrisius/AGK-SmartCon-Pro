using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Extracts <see cref="FamilySnapshot"/> and <see cref="SystemFamilySnapshot"/>
/// from open Revit documents for content-hash computation. All methods must be
/// called on the Revit UI thread (I-01) — the caller is responsible for
/// marshalling via <c>IFamilyManagerAwaitableEvent.RaiseAsync</c>.
/// </summary>
public sealed class RevitFamilySnapshotExtractor : IFamilySnapshotExtractor
{
    public FamilySnapshot ExtractFromFamilyDocument(Document familyDoc)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(familyDoc);
#else
        if (familyDoc is null) throw new ArgumentNullException(nameof(familyDoc));
#endif

        if (!familyDoc.IsFamilyDocument)
            throw new InvalidOperationException("Document is not a family document.");

        using var _scope = SmartConLogger.BeginScope("SnapshotExtract",
            ("Method", nameof(ExtractFromFamilyDocument)));

        var fm = familyDoc.FamilyManager;
        var familyName = familyDoc.Title;
        var category = familyDoc.OwnerFamily?.FamilyCategory?.Name ?? string.Empty;

        var parameters = ExtractParameters(fm);
        var types = ExtractTypes(fm, familyDoc);
        var geometry = ExtractGeometry(familyDoc);
        var sharedNested = ExtractSharedNestedNames(familyDoc);

        SmartConLogger.Info(
            $"Family snapshot: '{familyName}', {parameters.Count} params, " +
            $"{types.Count} types, {geometry.TotalFormCount} forms, " +
            $"{sharedNested.Count} shared nested");

        return new FamilySnapshot(
            FamilyName: familyName,
            Category: category,
            Parameters: parameters,
            Types: types,
            Geometry: geometry,
            SharedNestedFamilyNames: sharedNested);
    }

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

        SmartConLogger.Info(
            $"System snapshot: '{categoryName}', {sortedTypes.Count} types");

        return new SystemFamilySnapshot(
            CategoryName: categoryName,
            CategoryId: categoryId,
            Types: sortedTypes);
    }

    private static List<FamilyParameterInfo> ExtractParameters(
        Autodesk.Revit.DB.FamilyManager fm)
    {
        var rawParams = fm.GetParameters();
        var result = new List<FamilyParameterInfo>(rawParams.Count);

        foreach (var param in rawParams)
        {
            var name = param.Definition?.Name ?? string.Empty;
            if (string.IsNullOrEmpty(name)) continue;

            var storageType = param.StorageType.ToString();
            var group = GetParameterGroup(param);
            var isInstance = param.IsInstance;
            var isShared = param.IsShared;
            var formula = param.Formula;
            var isDeterminedByFormula = param.IsDeterminedByFormula;
            var isReporting = param.IsReporting;
            var guid = param.GUID != Guid.Empty ? param.GUID.ToString() : null;
            var builtInId = TryGetBuiltInParameterId(param);

            result.Add(new FamilyParameterInfo(
                Name: name,
                StorageType: storageType,
                ParameterGroup: group,
                IsInstance: isInstance,
                IsShared: isShared,
                Formula: formula,
                IsDeterminedByFormula: isDeterminedByFormula,
                IsReporting: isReporting,
                SharedParamGuid: guid,
                BuiltInParameterId: builtInId));
        }

        return result
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.StorageType, StringComparer.Ordinal)
            .ToList();
    }

    private static List<FamilyTypeSnapshot> ExtractTypes(
        Autodesk.Revit.DB.FamilyManager fm, Document familyDoc)
    {
        var paramMap = new Dictionary<string, FamilyParameter>(StringComparer.Ordinal);
        foreach (FamilyParameter param in fm.GetParameters())
        {
            if (param.Definition?.Name is string name)
                paramMap[name] = param;
        }

        var result = new List<FamilyTypeSnapshot>();

        foreach (FamilyType familyType in fm.Types)
        {
            if (string.IsNullOrWhiteSpace(familyType.Name))
                continue;

            var values = new List<FamilyParameterValue>();

            foreach (var pair in paramMap)
            {
                var value = ExtractParameterValue(familyType, pair.Value, pair.Key, familyDoc);
                values.Add(value);
            }

            var sortedValues = values
                .OrderBy(v => v.ParameterName, StringComparer.Ordinal)
                .ToList();

            result.Add(new FamilyTypeSnapshot(
                Name: familyType.Name,
                Values: sortedValues));
        }

        return result
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static FamilyParameterValue ExtractParameterValue(
        FamilyType familyType,
        FamilyParameter param,
        string parameterName,
        Document familyDoc)
    {
        var storageType = param.StorageType.ToString();

        if (!familyType.HasValue(param))
        {
            return new FamilyParameterValue(
                ParameterName: parameterName,
                StorageType: storageType,
                HasValue: false,
                ValueText: null,
                ValueNumber: null,
                ResolvedElementName: null);
        }

        try
        {
            string? valueText = null;
            double? valueNumber = null;
            string? resolvedName = null;

            switch (param.StorageType)
            {
                case StorageType.Double:
                    var dblVal = familyType.AsDouble(param);
                    valueNumber = dblVal;
                    valueText = FormattableString.Invariant($"{dblVal:0.######}");
                    break;

                case StorageType.Integer:
                    var intVal = familyType.AsInteger(param);
                    valueNumber = intVal;
                    valueText = intVal.ToString();
                    break;

                case StorageType.String:
                    var strVal = familyType.AsString(param);
                    valueText = strVal ?? string.Empty;
                    break;

                case StorageType.ElementId:
                    var elemId = familyType.AsElementId(param);
                    if (elemId is not null && elemId != ElementId.InvalidElementId)
                    {
                        var elem = familyDoc.GetElement(elemId);
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

            return new FamilyParameterValue(
                ParameterName: parameterName,
                StorageType: storageType,
                HasValue: true,
                ValueText: valueText,
                ValueNumber: valueNumber,
                ResolvedElementName: resolvedName);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Failed to read parameter '{parameterName}' (StorageType={storageType}): " +
                $"{ex.GetType().Name}: {ex.Message} " +
                "[Action: parameter will be recorded as error value in hash]");
            return new FamilyParameterValue(
                ParameterName: parameterName,
                StorageType: storageType,
                HasValue: true,
                ValueText: "READERROR",
                ValueNumber: null,
                ResolvedElementName: null);
        }
    }

    private static GeometryMetrics ExtractGeometry(Document familyDoc)
    {
        try
        {
            var forms = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(GenericForm))
                .Cast<GenericForm>()
                .ToList();

            if (forms.Count == 0)
            {
                SmartConLogger.Debug("No GenericForm elements found in family document");
                return new GeometryMetrics(0, Array.Empty<FormMetrics>());
            }

            var options = new Options
            {
                ComputeReferences = false,
                DetailLevel = ViewDetailLevel.Fine
            };

            var metricsList = new List<FormMetrics>(forms.Count);

            foreach (var form in forms)
            {
                var metric = ExtractFormMetrics(form, options);
                metricsList.Add(metric);
            }

            var sortedMetrics = metricsList
                .OrderBy(f => f.FormKind, StringComparer.Ordinal)
                .ThenBy(f => f.IsSolid)
                .ThenBy(f => f.Volume)
                .ToList();

            SmartConLogger.Debug(
                $"Geometry: {forms.Count} forms, " +
                $"{sortedMetrics.Count(f => f.IsSolid)} solid, " +
                $"{sortedMetrics.Count(f => !f.IsSolid)} void");

            return new GeometryMetrics(forms.Count, sortedMetrics);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Geometry extraction failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: hash will use 0 forms — check family document for corruption]");
            return new GeometryMetrics(0, Array.Empty<FormMetrics>());
        }
    }

    private static FormMetrics ExtractFormMetrics(GenericForm form, Options options)
    {
        var formKind = form.GetType().Name;
        var isSolid = form.IsSolid;
        double volume = 0;
        int faceCount = 0;
        int edgeCount = 0;
        string? subcategoryName = null;

        try
        {
            var geomElem = form.get_Geometry(options);
            if (geomElem is not null)
            {
                foreach (var geomObj in geomElem)
                {
                    if (geomObj is Solid solid && solid.Volume > 0)
                    {
                        volume += solid.Volume;
                        faceCount += solid.Faces.Size;
                        edgeCount += solid.Edges.Size;
                    }
                    else if (geomObj is GeometryInstance geomInst)
                    {
                        var transformedGeom = geomInst.GetInstanceGeometry();
                        if (transformedGeom is not null)
                        {
                            foreach (var innerObj in transformedGeom)
                            {
                                if (innerObj is Solid innerSolid && innerSolid.Volume > 0)
                                {
                                    volume += innerSolid.Volume;
                                    faceCount += innerSolid.Faces.Size;
                                    edgeCount += innerSolid.Edges.Size;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Geometry read failed for form '{formKind}' (Id={form.Id}): {ex.Message}");
        }

        try
        {
            subcategoryName = form.Subcategory?.Name;
        }
        catch
        {
            subcategoryName = null;
        }

        return new FormMetrics(
            FormKind: formKind,
            IsSolid: isSolid,
            Volume: volume,
            FaceCount: faceCount,
            EdgeCount: edgeCount,
            SubcategoryName: subcategoryName);
    }

    private static IReadOnlyList<string> ExtractSharedNestedNames(Document familyDoc)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        try
        {
            var collector = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(FamilyInstance));

            foreach (FamilyInstance fi in collector)
            {
                var family = fi.Symbol?.Family;
                if (family is null) continue;

                if (!IsSharedFamily(family)) continue;

                var name = family.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!seen.Add(name)) continue;

                result.Add(name);
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Shared nested scan failed: {ex.Message}");
        }

        return result
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsSharedFamily(Autodesk.Revit.DB.Family family)
    {
        try
        {
            var p = family.get_Parameter(BuiltInParameter.FAMILY_SHARED);
            return p is not null && p.AsInteger() == 1;
        }
        catch
        {
            return false;
        }
    }

    private static SystemTypeSnapshot ExtractSystemType(
        ElementType elementType, Document projectDoc)
    {
        var name = elementType.Name;
        var rawParams = elementType.GetOrderedParameters();
        var values = new List<SystemParameterValue>(rawParams.Count);

        foreach (var param in rawParams)
        {
            var paramName = param.Definition?.Name;
            if (string.IsNullOrEmpty(paramName)) continue;

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

                switch (param.StorageType)
                {
                    case StorageType.Double:
                        var dblVal = param.AsDouble();
                        valueNumber = dblVal;
                        valueText = FormattableString.Invariant($"{dblVal:0.######}");
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
                    ResolvedElementName: resolvedName));
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

        return new SystemTypeSnapshot(
            Name: name,
            Values: sortedValues);
    }

    private static string GetParameterGroup(FamilyParameter param)
    {
#if REVIT2024_OR_GREATER
        try
        {
            var groupTypeId = param.Definition?.GetGroupTypeId();
            return groupTypeId?.TypeId ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
#else
        try
        {
            return param.Definition?.ParameterGroup.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
#endif
    }

    private static string? TryGetBuiltInParameterId(FamilyParameter param)
    {
        try
        {
            var idValue = param.Id;
#if REVIT2024_OR_GREATER
            var idInt = (int)idValue.Value;
#else
            var idInt = idValue.IntegerValue;
#endif
            if (idInt < 0 && Enum.IsDefined(typeof(BuiltInParameter), idInt))
            {
                return ((BuiltInParameter)idInt).ToString();
            }
        }
        catch
        {
        }
        return null;
    }
}

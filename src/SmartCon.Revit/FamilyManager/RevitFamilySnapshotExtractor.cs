using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Util;

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
            ("Method", nameof(ExtractFromFamilyDocument)),
            ("FamilyDocTitle", familyDoc.Title),
            ("FamilyDocPath", string.IsNullOrEmpty(familyDoc.PathName) ? "<empty>" : Path.GetFileName(familyDoc.PathName)),
            ("IsValidObject", familyDoc.IsValidObject));

        var fm = familyDoc.FamilyManager;
        var familyName = familyDoc.Title;
        if (familyName.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
            familyName = familyName[..^4];
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

    public IReadOnlyList<FamilyGeometryPerType> ExtractGeometryPerType(
        Document familyDoc,
        CancellationToken ct = default)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(familyDoc);
#else
        if (familyDoc is null) throw new ArgumentNullException(nameof(familyDoc));
#endif

        using var _scope = SmartConLogger.BeginScope("Geo3DPerType",
            ("Method", nameof(ExtractGeometryPerType)));

        var result = new List<FamilyGeometryPerType>();
        var familyName = familyDoc.Title;
        if (familyName.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
            familyName = familyName[..^4];

        var fm = familyDoc.FamilyManager;
        var typeCount = fm.Types.Size;

        SmartConLogger.Info($"ExtractGeometryPerType: familyName='{familyName}', typeCount={typeCount}");

        if (typeCount <= 1)
        {
            ct.ThrowIfCancellationRequested();
            var typeName = fm.CurrentType?.Name ?? "";
            if (string.IsNullOrWhiteSpace(typeName))
            {
                // Family with no types created (typeCount=0) OR a single unnamed
                // default type (typeCount=1, Name=""). This is a normal family
                // where the user didn't create explicit types — the geometry
                // lives in the family document itself, not in any type.
                // Jeremy Tammik (The Building Coder): "A family loaded into a
                // project that has no types in it will get a default type
                // assigned to it in the project editor that's the same as the
                // short family file name." We mirror that here: use familyName
                // as the type name so the viewer and tree show it consistently.
                // Previously this returned an empty result, skipping geometry
                // extraction entirely — the 3D viewer showed nothing.
                typeName = familyName;
                SmartConLogger.Info(
                    $"ExtractGeometryPerType: no named type — using family name '{familyName}' as type name");
            }

            var meshes = RevitFamilyGeometryExtractor.ExtractMeshesFromFamilyDoc(familyDoc, ct);
            result.Add(new FamilyGeometryPerType(typeName, familyName, meshes));
            SmartConLogger.Info(
                $"ExtractGeometryPerType: single type '{typeName}' → {meshes.Count} meshes, " +
                $"{(meshes.Count > 0 ? meshes.Sum(m => m.TriangleCount) : 0)} triangles");
            return result;
        }

        // Multiple types: iterate with Transaction+RollBack (I-03b).
        // Precedent: RevitFamilyDataExtractionService.cs:227 uses the
        // same pattern for temp type creation in family documents.
        FamilyType? originalType = null;
        try { originalType = fm.CurrentType; }
        catch { }

        using (var tx = new Transaction(familyDoc, "SmartCon_GeometryPerType"))
        {
            tx.Start();
            SmartConLogger.Debug(
                "Geo3DPerType: Transaction.Started, familyDoc.IsModified=" + familyDoc.IsModified
                + ", IsReadOnly=" + familyDoc.IsReadOnly
                + ", IsValidObject=" + familyDoc.IsValidObject);
            try
            {
                foreach (FamilyType ft in fm.Types)
                {
                    ct.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(ft.Name))
                    {
                        SmartConLogger.Debug("ExtractGeometryPerType: skipping unnamed default type");
                        continue;
                    }

                    try
                    {
                        var typeBefore = fm.CurrentType?.Name ?? "<none>";
                        fm.CurrentType = ft;
                        var typeAfter = fm.CurrentType?.Name ?? "<none>";
                        SmartConLogger.Debug(
                            $"ExtractGeometryPerType: switched type '{ft.Name}' (before='{typeBefore}', after='{typeAfter}')");
                    }
                    catch (Exception ex)
                    {
                        SmartConLogger.Warn(
                            $"ExtractGeometryPerType: failed to switch to type '{ft.Name}': {ex.Message} " +
                            "[Action: type geometry will be skipped]");
                        continue;
                    }

                    // NOTE: Regenerate() removed — Jeremy Tammik (Autodesk):
                    // "Whenever a transaction is committed, Revit regenerates
                    // the document for you anyway." Inside an uncommitted
                    // transaction, calling Regenerate on a held-open family
                    // document triggers Revit's view-update machinery, which
                    // on net48 (Revit 2019-2024) can leave the WPF render
                    // thread in a zombie state — the next ShowDialog then
                    // blocks ~9 seconds waiting for the render thread to pump
                    // paint messages (intermittent, depending on whether
                    // layout completed before the held-open document state
                    // was modified). Switching fm.CurrentType already updates
                    // the in-memory family model; get_Geometry sees the new
                    // type's parameter values without explicit Regenerate.
                    // See: docs/adr/042-familymanager-3d-preview.md (net48
                    // white-dialog bug).

                    var meshes = RevitFamilyGeometryExtractor.ExtractMeshesFromFamilyDoc(familyDoc, ct);

                    if (meshes.Count > 0 && !meshes.All(m => m.IsEmpty))
                    {
                        var triCount = meshes.Sum(m => m.TriangleCount);
                        result.Add(new FamilyGeometryPerType(ft.Name, familyName, meshes));
                        SmartConLogger.Info(
                            $"ExtractGeometryPerType: type '{ft.Name}' → {meshes.Count} meshes, {triCount} triangles");
                    }
                    else
                    {
                        SmartConLogger.Info(
                            $"ExtractGeometryPerType: type '{ft.Name}' → no geometry extracted");
                    }
                }
            }
            finally
            {
                if (originalType is not null)
                {
                    try { fm.CurrentType = originalType; }
                    catch { }
                }
                SmartConLogger.Debug(
                    "Geo3DPerType: before RollBack, familyDoc.IsModified=" + familyDoc.IsModified);
                tx.RollBack();
                SmartConLogger.Debug(
                    "Geo3DPerType: after RollBack, familyDoc.IsModified=" + familyDoc.IsModified
                    + ", IsReadOnly=" + familyDoc.IsReadOnly);
            }
        }

        SmartConLogger.Info(
            $"ExtractGeometryPerType: extracted {result.Count}/{typeCount} types with geometry for '{familyName}'");

        return result;
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

    private static List<FamilyParameterInfo> ExtractParameters(
        Autodesk.Revit.DB.FamilyManager fm)
    {
        var rawParams = fm.GetParameters();
        SmartConLogger.Debug($"ExtractParameters: fm.GetParameters() returned {rawParams.Count} parameter(s)");
        var result = new List<FamilyParameterInfo>(rawParams.Count);

        foreach (var param in rawParams)
        {
            var name = param.Definition?.Name ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                SmartConLogger.Debug($"  ExtractParameters: skipping param with empty Definition.Name (StorageType={param.StorageType}, IsShared={param.IsShared}, BuiltInId={TryGetBuiltInParameterId(param) ?? "<none>"})");
                continue;
            }

            var storageType = param.StorageType.ToString();
            var group = GetParameterGroup(param);
            var isInstance = param.IsInstance;
            var isShared = param.IsShared;
            var formula = param.Formula;
            var isDeterminedByFormula = param.IsDeterminedByFormula;
            var isReporting = param.IsReporting;
            string? guid = null;
            try
            {
                if (param.IsShared && param.GUID != Guid.Empty)
                    guid = param.GUID.ToString();
            }
            catch { }
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
        var totalTypes = fm.Types.Size;
        SmartConLogger.Debug($"ExtractTypes: fm.Types.Size={totalTypes}");
        var paramMap = new Dictionary<string, FamilyParameter>(StringComparer.Ordinal);
        foreach (FamilyParameter param in fm.GetParameters())
        {
            if (param.Definition?.Name is string name)
                paramMap[name] = param;
        }

        // Phase 27: lookup FamilySymbol by type name so we can capture the
        // Revit UniqueId for each type. This mirrors LoadableFamilyTypeResolver
        // (ResolveTypesFromRfa) — the UniqueId is needed by the snapshot-to-DB
        // mapper to populate FamilyTypeDescriptor.UniqueId without re-opening
        // the .rfa in Commit. Collecting it here (in the single Prepare open)
        // eliminates the 42× LoadableResolver.OpenDocumentFile calls seen in
        // the post-import flow.
        var symbolsByName = new FilteredElementCollector(familyDoc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .ToLookup(s => s.Name, s => s, StringComparer.Ordinal);

        var result = new List<FamilyTypeSnapshot>();

        foreach (FamilyType familyType in fm.Types)
        {
            if (string.IsNullOrWhiteSpace(familyType.Name))
            {
                SmartConLogger.Debug("  ExtractTypes: skipping unnamed default type");
                continue;
            }

            var typeName = familyType.Name;
            var values = new List<FamilyParameterValue>();

            foreach (var pair in paramMap)
            {
                var value = ExtractParameterValue(familyType, pair.Value, pair.Key, familyDoc);
                values.Add(value);
            }

            var sortedValues = values
                .OrderBy(v => v.ParameterName, StringComparer.Ordinal)
                .ToList();

            var uniqueId = symbolsByName[typeName].FirstOrDefault()?.UniqueId;

            result.Add(new FamilyTypeSnapshot(
                Name: typeName,
                Values: sortedValues,
                UniqueId: uniqueId));
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

            var symbolicCount = CountElements(familyDoc,
                new CurveElementFilter(CurveElementType.SymbolicCurve));
            var detailCount = CountElements(familyDoc,
                new CurveElementFilter(CurveElementType.DetailCurve));
            var modelCount = CountElements(familyDoc,
                new CurveElementFilter(CurveElementType.ModelCurve));
            var textNoteCount = CountElements(familyDoc, typeof(TextNote));
            var refPlaneCount = CountElements(familyDoc, typeof(ReferencePlane));
            var dimensionCount = CountElements(familyDoc, typeof(Dimension));

            if (forms.Count == 0)
            {
                SmartConLogger.Debug(
                    $"No GenericForm elements found in family document. " +
                    $"2D: symbolic={symbolicCount}, detail={detailCount}, model={modelCount}, " +
                    $"text={textNoteCount}, refPlane={refPlaneCount}, dim={dimensionCount}");
                return new GeometryMetrics(
                    0, Array.Empty<FormMetrics>(),
                    symbolicCount, detailCount, modelCount,
                    textNoteCount, refPlaneCount, dimensionCount);
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
                $"{sortedMetrics.Count(f => !f.IsSolid)} void. " +
                $"2D: symbolic={symbolicCount}, detail={detailCount}, model={modelCount}, " +
                $"text={textNoteCount}, refPlane={refPlaneCount}, dim={dimensionCount}");

            return new GeometryMetrics(
                forms.Count, sortedMetrics,
                symbolicCount, detailCount, modelCount,
                textNoteCount, refPlaneCount, dimensionCount);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Geometry extraction failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: hash will use 0 forms — check family document for corruption]");
            return new GeometryMetrics(0, Array.Empty<FormMetrics>());
        }
    }

    private static int CountElements(Document doc, ElementFilter filter)
    {
        try
        {
            return new FilteredElementCollector(doc)
                .WherePasses(filter)
                .ToElements().Count;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountElements(Document doc, Type elementType)
    {
        try
        {
            return new FilteredElementCollector(doc)
                .OfClass(elementType)
                .ToElements().Count;
        }
        catch
        {
            return 0;
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

        var paramDict = new SortedDictionary<string, Parameter>(StringComparer.Ordinal);
        foreach (Parameter param in elementType.Parameters)
        {
            var pname = param.Definition?.Name;
            if (string.IsNullOrEmpty(pname)) continue;
            paramDict[pname!] = param;
        }

        SmartConLogger.Debug(
            $"ExtractSystemType '{name}': {paramDict.Count} params from Element.Parameters: " +
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
            Values: sortedValues);
    }

    private static bool IsBlankValue(SystemParameterValue v)
    {
        if (!v.HasValue) return true;
        var text = v.ValueText;
        if (text is null) return false;
        return text.Length == 0
            || text == "INVALID"
            || text == "UNSUPPORTED"
            || text == "READERROR";
    }

    private static bool IsAutoGeneratedName(string parameterName)
    {
        if (string.IsNullOrEmpty(parameterName)) return false;
        return ContainsOrdinalIgnoreCase(parameterName, "IfcGUID")
            || ContainsOrdinalIgnoreCase(parameterName, "IFC GUID");
    }

    private static bool ContainsOrdinalIgnoreCase(string haystack, string needle)
    {
#if NET8_0_OR_GREATER
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
#else
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
#endif
    }

    private static string FormatValuePreview(SystemParameterValue v)
    {
        if (!v.HasValue) return "EMPTY";
        if (v.ValueNumber.HasValue)
            return FormattableString.Invariant($"{v.ValueNumber.Value:0.######}");
        return v.ValueText is null ? "?" : $"\"{v.ValueText}\"";
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

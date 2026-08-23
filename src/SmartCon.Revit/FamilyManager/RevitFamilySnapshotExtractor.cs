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

        ParameterUnitDiagnostics.LogDocumentUnits(familyDoc, "SnapshotExtract");

        var fm = familyDoc.FamilyManager;
        var familyName = familyDoc.Title;
        if (familyName.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
            familyName = familyName[..^4];
        var familyCategory = familyDoc.OwnerFamily?.FamilyCategory;
        var category = familyCategory?.Name ?? string.Empty;
        var categoryId = GetCategoryOrdinal(familyCategory);
        var facts = ExtractFacts(familyDoc, categoryId);

        var parameters = ExtractParameters(fm);
        var types = ExtractTypes(fm, familyDoc);
        var phantomValues = ExtractPhantomTypeValues(fm, familyDoc);
        var geometry = ExtractGeometry(familyDoc);
        var (sharedNested, nonSharedNested) = ExtractNestedNames(familyDoc);
        var connectors = ExtractConnectors(familyDoc);
        var behaviorFlags = ExtractBehaviorFlags(familyDoc);
        var lookupTables = ExtractLookupTables(familyDoc);

        SmartConLogger.Info(
            $"Family snapshot: '{familyName}', {parameters.Count} params, " +
            $"{types.Count} types, {geometry.TotalFormCount} forms, " +
            $"{sharedNested.Count} shared nested, {nonSharedNested.Count} non-shared nested, " +
            $"{connectors.Count} connectors" +
            $"{(facts.Count > 0 ? $", {facts.Count} facts" : string.Empty)}" +
            $"{(lookupTables is { Count: > 0 } ? $", {lookupTables.Count} lookup tables" : string.Empty)}");

        return new FamilySnapshot(
            FamilyName: familyName,
            Category: category,
            Parameters: parameters,
            Types: types,
            Geometry: geometry,
            SharedNestedFamilyNames: sharedNested,
            CategoryId: categoryId,
            Facts: facts.Count > 0 ? facts : null,
            Connectors: connectors.Count > 0 ? connectors : null,
            BehaviorFlags: behaviorFlags,
            NonSharedNestedFamilyNames: nonSharedNested.Count > 0 ? nonSharedNested : null,
            PhantomTypeValues: phantomValues is { Count: > 0 } ? phantomValues : null,
            LookupTables: lookupTables);
    }

    /// <summary>
    /// FHV11 (Issue #238): reads the family's embedded lookup tables
    /// (таблицы поиска, <c>FamilySizeTable</c>) as raw CSV content for the
    /// LOOKUP hash section. Uses <c>ExportSizeTable</c> to a temp file
    /// instead of in-memory <c>AsValueString</c> cell reads: the exported
    /// CSV is Revit's machine-oriented format (<c>##spec##unit</c> headers,
    /// raw values) — locale-invariant by construction, while
    /// <c>AsValueString</c> is display formatting. Works without a
    /// transaction (probe-proven 2026-08-23) and in any family-document
    /// context: raw .rfa open, EditFamily copy (embedded verification) and
    /// the actualization engine — the manager is fetched per call.
    /// Returns <c>null</c> for table-less families (the LOOKUP section is
    /// then omitted, keeping the hash stable for them).
    /// </summary>
    private static IReadOnlyList<LookupTableSnapshot>? ExtractLookupTables(Document familyDoc)
    {
        try
        {
            var ownerFamilyId = familyDoc.OwnerFamily?.Id;
            if (ownerFamilyId is null)
                return null;

            var fstm = FamilySizeTableManager.GetFamilySizeTableManager(familyDoc, ownerFamilyId);
            if (fstm is null || fstm.NumberOfSizeTables == 0)
                return null;

            var result = new List<LookupTableSnapshot>();
            foreach (var tableName in fstm.GetAllSizeTableNames().OrderBy(n => n, StringComparer.Ordinal))
            {
                var csv = ExportSizeTableToString(fstm, tableName);
                if (csv is not null)
                {
                    result.Add(new LookupTableSnapshot(tableName, csv));
                }
                else
                {
                    SmartConLogger.Warn(
                        $"Lookup table '{tableName}': ExportSizeTable failed — the table is EXCLUDED from the content hash. " +
                        "[Action: changes to this table will not shift the hash; re-export the lookup table in the family editor]");
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Lookup table extraction failed: {ex.GetType().Name}: {ex.Message}. " +
                "[Action: the family hash excludes lookup tables for this run — re-open the family and retry]");
            return null;
        }
    }

    private static string? ExportSizeTableToString(FamilySizeTableManager fstm, string tableName)
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            if (!fstm.ExportSizeTable(tableName, tempPath))
                return null;

            // Normalize: line endings + trailing whitespace, so the same
            // table content hashes identically regardless of the export
            // environment. (Plain Replace — ordinal; the StringComparison
            // overload does not exist on net48.)
            return File.ReadAllText(tempPath)
                .Replace("\r\n", "\n")
                .TrimEnd();
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"ExportSizeTable('{tableName}') threw: {ex.Message}");
            return null;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* temp file cleanup */ }
        }
    }

    /// <summary>
    /// FHV9 (#209 stress test 2026-08-12): parameter values of a TYPELESS
    /// family (no named types). FHV8 skipped the phantom default type
    /// entirely, which dropped value coverage for typeless families — an
    /// edit of any non-geometric value (e.g. the built-in «Модель») never
    /// changed the content hash and the import dialog reported a false
    /// Duplicate. The phantom's values are context-stable when read the
    /// right way: an EditFamily/editor document carries the unnamed current
    /// type (Size=1); a raw background open reports Size=0 with no current
    /// type, so a temporary type is synthesized inside a transaction that is
    /// rolled back (probe-proven 2026-08-12: NewType works at Size=0,
    /// RollBack restores Size=0, no trace). Both contexts read the same
    /// default values, so identity hashes stay comparable across contexts.
    /// Returns <c>null</c> when the family has named types (values live in
    /// the TYPES section) or no parameters.
    /// </summary>
    private static List<FamilyParameterValue>? ExtractPhantomTypeValues(
        Autodesk.Revit.DB.FamilyManager fm, Document familyDoc)
    {
        var allTypes = fm.Types.Cast<FamilyType>().ToList();
        if (allTypes.Any(t => !string.IsNullOrWhiteSpace(t.Name)))
        {
            return null;
        }

        var parameters = fm.GetParameters()
            .Where(p => p.Definition?.Name is not null)
            .ToList();
        if (parameters.Count == 0)
        {
            return null;
        }

        if (allTypes.Count == 1)
        {
            return ReadPhantomValues(allTypes[0], parameters, familyDoc, "current phantom");
        }

        try
        {
            List<FamilyParameterValue>? values = null;
            using (var tx = new Transaction(familyDoc, "SmartCon_PhantomValues"))
            {
                tx.Start();
                try
                {
                    var synth = fm.NewType("__SmartConPhantomValues__");
                    fm.CurrentType = synth;
                    values = ReadPhantomValues(synth, parameters, familyDoc, "synthesized");
                }
                finally
                {
                    tx.RollBack();
                }
            }
            return values;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Phantom value synthesis failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: значения typeless-семейства не попадут в хэш этого извлечения — проверьте, что документ не находится внутри другой транзакции]");
            return null;
        }
    }

    private static List<FamilyParameterValue> ReadPhantomValues(
        FamilyType type,
        List<FamilyParameter> parameters,
        Document familyDoc,
        string tag)
    {
        var values = new List<FamilyParameterValue>(parameters.Count);
        foreach (var param in parameters)
        {
            values.Add(ExtractParameterValue(type, param, param.Definition.Name, familyDoc));
        }
        SmartConLogger.Debug($"ExtractPhantomValues ({tag}): read {values.Count} value(s)");
        return values.OrderBy(v => v.ParameterName, StringComparer.Ordinal).ToList();
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

        if (result.Count == 0)
        {
            SmartConLogger.Warn(
                $"ExtractGeometryPerType: no geometry for any type in '{familyName}' (typeCount={typeCount}) " +
                "[Action: verify family has visible 3D solids; 3D preview will be unavailable]");
        }

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

    /// <summary>
    /// Category-driven facts per <see cref="FamilyFactRuleSet"/> (ADR-055).
    /// Every matched rule produces exactly one <see cref="FamilyFact"/> —
    /// a read-but-absent parameter becomes the documented empty-string
    /// sentinel so the actualization task's detection clears and the UI
    /// hides the row. Read failures are swallowed into the sentinel as
    /// well: facts are cosmetic metadata and must never break a snapshot.
    /// </summary>
    private static List<FamilyFact> ExtractFacts(Document familyDoc, int? categoryId)
    {
        var result = new List<FamilyFact>();
        if (categoryId is null) return result;

        var rules = FamilyFactRuleSet.GetRulesForCategory(categoryId.Value);
        if (rules.Count == 0) return result;

        foreach (var rule in rules)
        {
            result.Add(ReadFact(familyDoc, rule));
        }
        return result;
    }

    private static FamilyFact ReadFact(Document familyDoc, FamilyFactRule rule)
    {
        // Sentinel: the fact was evaluated but the source parameter is
        // absent/unset in this family — detection clears, UI hides.
        var sentinel = new FamilyFact(rule.FactKey, string.Empty, string.Empty);

        try
        {
            var param = familyDoc.OwnerFamily?.get_Parameter((BuiltInParameter)rule.ParameterId);
            if (param is null || !param.HasValue)
            {
                SmartConLogger.Debug(
                    $"ExtractFacts: '{rule.FactKey}' parameter unavailable in '{familyDoc.Title}' — sentinel written");
                return sentinel;
            }

            switch (param.StorageType)
            {
                case StorageType.Integer:
                    var intVal = param.AsInteger();
                    // Part Type reads as a raw int; the enum member name is
                    // the stable human fallback (AsValueString would return
                    // the bare number for this parameter).
                    var display = rule.FactKey == FamilyFactRuleSet.PartTypeFactKey
                        ? ((PartType)intVal).ToString()
                        : intVal.ToString(CultureInfo.InvariantCulture);
                    return new FamilyFact(
                        rule.FactKey,
                        intVal.ToString(CultureInfo.InvariantCulture),
                        display);

                case StorageType.String:
                    var strVal = param.AsString();
                    return string.IsNullOrEmpty(strVal)
                        ? sentinel
                        : new FamilyFact(rule.FactKey, strVal, strVal);

                default:
                    return sentinel;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"ExtractFacts: read of '{rule.FactKey}' failed in '{familyDoc.Title}': {ex.Message}");
            return sentinel;
        }
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
        var allTypes = fm.Types.Cast<FamilyType>().ToList();

        foreach (FamilyType familyType in allTypes)
        {
            string typeName;
            if (string.IsNullOrWhiteSpace(familyType.Name))
            {
                // FHV8 (#209): the unnamed default type is ALWAYS skipped in
                // the TYPES section. It is a phantom Revit synthesizes when
                // a typeless family is LOADED into a document: the raw .rfa
                // reports Types.Size=0 while an EditFamily copy of the same
                // family reports Size=1 with this unnamed type — extracting
                // it (formerly as '<default>') made the hash depend on the
                // extraction context (raw open vs post-load copy) and broke
                // import↔migration and file↔nested dedup equality. FHV9:
                // the phantom's VALUES are extracted separately and
                // context-stably by ExtractPhantomTypeValues (PHANTOM
                // section of the identity hash) — the type entry itself
                // stays skipped here. The synthetic
                // FamilyTypeSnapshot.DefaultTypeName constant remains for
                // legacy DB rows and the display rule only.
                SmartConLogger.Debug("  ExtractTypes: skipping unnamed default type (phantom, FHV8)");
                continue;
            }
            else
            {
                typeName = familyType.Name;
            }
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
            string? valueDisplay = null;
            string? specTypeId = null;
            string? unitTypeId = null;

            switch (param.StorageType)
            {
                case StorageType.Double:
                    var dblVal = familyType.AsDouble(param);
                    valueNumber = dblVal;
                    valueText = FormattableString.Invariant($"{dblVal:0.######}");
                    if (dblVal.HasValue)
                    {
                        valueDisplay = Compatibility.RevitUnitsCompat.FormatDisplayValue(familyDoc, param, dblVal.Value);
                        specTypeId = Compatibility.RevitUnitsCompat.GetSpecTypeIdString(param.Definition);
                        unitTypeId = Compatibility.RevitUnitsCompat.GetUnitTypeIdString(param);
                    }
                    ParameterUnitDiagnostics.LogFamilyTypeDouble(familyType, param, parameterName, dblVal, "Snapshot");
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
                ResolvedElementName: resolvedName,
                ValueDisplay: valueDisplay,
                SpecTypeId: specTypeId,
                UnitTypeId: unitTypeId);
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

            var (symbolicCount, symbolicLength) = CountAndMeasureCurves(familyDoc,
                new CurveElementFilter(CurveElementType.SymbolicCurve));
            var (detailCount, detailLength) = CountAndMeasureCurves(familyDoc,
                new CurveElementFilter(CurveElementType.DetailCurve));
            var (modelCount, modelLength) = CountAndMeasureCurves(familyDoc,
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
                    textNoteCount, refPlaneCount, dimensionCount,
                    symbolicLength, detailLength, modelLength);
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
                textNoteCount, refPlaneCount, dimensionCount,
                symbolicLength, detailLength, modelLength);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Geometry extraction failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: hash will use 0 forms — check family document for corruption]");
            return new GeometryMetrics(0, Array.Empty<FormMetrics>());
        }
    }

    /// <summary>
    /// Count curve elements matching the filter and sum their geometry
    /// curve lengths (ADR-056). Length catches 2D edits that keep the
    /// element count constant (redrawn line of the same kind).
    /// </summary>
    private static (int Count, double TotalLength) CountAndMeasureCurves(Document doc, ElementFilter filter)
    {
        try
        {
            var count = 0;
            double length = 0;
            foreach (var element in new FilteredElementCollector(doc).WherePasses(filter))
            {
                count++;
                try
                {
                    if (element is CurveElement { GeometryCurve: not null } curveElement)
                        length += curveElement.GeometryCurve.Length;
                }
                catch
                {
                    // single curve unreadable — count stays, length skips it
                }
            }
            return (count, length);
        }
        catch
        {
            return (0, 0);
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
        double surfaceArea = 0;
        int faceCount = 0;
        int edgeCount = 0;
        string? subcategoryName = null;
        BoundingBoxSnapshot? bounds = null;

        try
        {
            var geomElem = form.get_Geometry(options);
            if (geomElem is not null)
            {
                foreach (var geomObj in geomElem)
                {
                    if (geomObj is Solid solid && solid.Volume > 0)
                    {
                        AccumulateSolid(solid, ref volume, ref surfaceArea, ref faceCount, ref edgeCount);
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
                                    AccumulateSolid(innerSolid, ref volume, ref surfaceArea, ref faceCount, ref edgeCount);
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
            var bbox = form.get_BoundingBox(null);
            if (bbox is not null)
            {
                bounds = new BoundingBoxSnapshot(
                    bbox.Min.X, bbox.Min.Y, bbox.Min.Z,
                    bbox.Max.X, bbox.Max.Y, bbox.Max.Z);
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Bounding box read failed for form '{formKind}' (Id={form.Id}): {ex.Message}");
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
            SubcategoryName: subcategoryName,
            SurfaceArea: surfaceArea,
            Bounds: bounds);
    }

    private static void AccumulateSolid(
        Solid solid, ref double volume, ref double surfaceArea, ref int faceCount, ref int edgeCount)
    {
        volume += solid.Volume;
        faceCount += solid.Faces.Size;
        edgeCount += solid.Edges.Size;
        try
        {
            foreach (Face face in solid.Faces)
            {
                surfaceArea += face.Area;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Face area accumulation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Nested family names split by the shared flag (ADR-056): shared
    /// nested families keep their long-standing hash role, non-shared
    /// ones join in FHV3 (their replacement is real content too).
    /// Single collector pass for both lists.
    /// </summary>
    private static (List<string> Shared, List<string> NonShared) ExtractNestedNames(Document familyDoc)
    {
        var seenShared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNonShared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shared = new List<string>();
        var nonShared = new List<string>();

        try
        {
            var collector = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(FamilyInstance));

            foreach (FamilyInstance fi in collector)
            {
                var family = fi.Symbol?.Family;
                if (family is null) continue;

                var name = family.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (IsSharedFamily(family))
                {
                    if (seenShared.Add(name))
                        shared.Add(name);
                }
                else
                {
                    if (seenNonShared.Add(name))
                        nonShared.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Nested family scan failed: {ex.Message}");
        }

        return (
            shared.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            nonShared.OrderBy(n => n, StringComparer.Ordinal).ToList());
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

    /// <summary>
    /// Connector elements of the family (ADR-056, Issue #159): domain,
    /// profile, sizes, system classification, origin and intra-family
    /// linkage. Returned pre-sorted (Domain, Shape, SystemClassification,
    /// Origin) with <see cref="ConnectorSnapshot.LinkedIndex"/> computed
    /// against that order — the hasher re-sorts with the identical key,
    /// which is a no-op for this list. Every property read is isolated:
    /// a connector whose size is not applicable to its profile yields
    /// <c>null</c>, never an exception.
    /// </summary>
    private static List<ConnectorSnapshot> ExtractConnectors(Document familyDoc)
    {
        try
        {
            var elements = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(ConnectorElement))
                .Cast<ConnectorElement>()
                .ToList();

            if (elements.Count == 0)
                return new List<ConnectorSnapshot>(0);

            var sorted = elements
                .OrderBy(el => SafeDomain(el))
                .ThenBy(el => SafeShape(el))
                .ThenBy(el => SafeSystemClassification(el))
                .ThenBy(el => SafeOrigin(el)?.X ?? 0)
                .ThenBy(el => SafeOrigin(el)?.Y ?? 0)
                .ThenBy(el => SafeOrigin(el)?.Z ?? 0)
                .ToList();

            var indexByElementId = new Dictionary<long, int>(sorted.Count);
            for (var i = 0; i < sorted.Count; i++)
            {
                indexByElementId[GetElementIdValue(sorted[i].Id)] = i;
            }

            var result = new List<ConnectorSnapshot>(sorted.Count);
            foreach (var el in sorted)
            {
                var origin = SafeOrigin(el);
                var linkedIndex = -1;
                try
                {
                    var linked = el.GetLinkedConnectorElement();
                    if (linked is not null &&
                        indexByElementId.TryGetValue(GetElementIdValue(linked.Id), out var found))
                    {
                        linkedIndex = found;
                    }
                }
                catch
                {
                    // no linked connector
                }

                result.Add(new ConnectorSnapshot(
                    Domain: SafeDomain(el),
                    Shape: SafeShape(el),
                    SystemClassification: SafeSystemClassification(el),
                    IsPrimary: SafeIsPrimary(el),
                    Width: SafeDimension(el, nameof(ConnectorElement.Width)),
                    Height: SafeDimension(el, nameof(ConnectorElement.Height)),
                    Radius: SafeDimension(el, nameof(ConnectorElement.Radius)),
                    OriginX: origin?.X ?? 0,
                    OriginY: origin?.Y ?? 0,
                    OriginZ: origin?.Z ?? 0,
                    LinkedIndex: linkedIndex));
            }

            return result;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Connector scan failed: {ex.Message}");
            return new List<ConnectorSnapshot>(0);
        }
    }

    private static int SafeDomain(ConnectorElement el)
    {
        try { return (int)el.Domain; }
        catch { return 0; }
    }

    private static int SafeShape(ConnectorElement el)
    {
        try { return (int)el.Shape; }
        catch { return 0; }
    }

    private static int SafeSystemClassification(ConnectorElement el)
    {
        try { return (int)el.SystemClassification; }
        catch { return 0; }
    }

    private static bool SafeIsPrimary(ConnectorElement el)
    {
        try { return el.IsPrimary; }
        catch { return false; }
    }

    private static XYZ? SafeOrigin(ConnectorElement el)
    {
        try { return el.Origin; }
        catch { return null; }
    }

    private static double? SafeDimension(ConnectorElement el, string propertyName)
    {
        try
        {
            return propertyName switch
            {
                nameof(ConnectorElement.Width) => el.Width,
                nameof(ConnectorElement.Height) => el.Height,
                nameof(ConnectorElement.Radius) => el.Radius,
                _ => null,
            };
        }
        catch
        {
            // dimension not applicable to this profile (e.g. Radius on rectangular)
            return null;
        }
    }

    private static long GetElementIdValue(ElementId id)
    {
#if REVIT2024_OR_GREATER
        return id.Value;
#else
        return id.IntegerValue;
#endif
    }

    /// <summary>
    /// Behavior flags from the <c>Family</c> element (ADR-056): shared,
    /// work-plane-based, always-vertical, cut-with-voids. These are
    /// built-in parameters on <c>OwnerFamily</c>, invisible to
    /// <c>FamilyManager.GetParameters()</c>.
    /// </summary>
    private static FamilyBehaviorFlags? ExtractBehaviorFlags(Document familyDoc)
    {
        var family = familyDoc.OwnerFamily;
        if (family is null) return null;

        return new FamilyBehaviorFlags(
            IsShared: ReadBoolFlag(family, BuiltInParameter.FAMILY_SHARED),
            IsWorkPlaneBased: ReadBoolFlag(family, BuiltInParameter.FAMILY_WORK_PLANE_BASED),
            IsAlwaysVertical: ReadBoolFlag(family, BuiltInParameter.FAMILY_ALWAYS_VERTICAL),
            AllowsCutWithVoids: ReadBoolFlag(family, BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS));
    }

    private static bool? ReadBoolFlag(Element element, BuiltInParameter builtInParameter)
    {
        try
        {
            var param = element.get_Parameter(builtInParameter);
            if (param is null || !param.HasValue || param.StorageType != StorageType.Integer)
                return null;
            return param.AsInteger() != 0;
        }
        catch
        {
            return null;
        }
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
                        ParameterUnitDiagnostics.LogParameterDouble(param, paramName!, dblVal, "SystemSnapshot");
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
    /// Compound structure (layer stack) of a host type — Basic walls,
    /// floors, roofs, ceilings (ADR-056). <c>null</c> for non-host types
    /// and for hosts without a compound structure (stacked/curtain
    /// walls): both are deterministic canonical states, distinct from
    /// each other only by the type itself.
    /// </summary>
    private static CompoundStructureSnapshot? ExtractCompoundStructure(
        ElementType elementType, Document doc)
    {
        if (elementType is not HostObjAttributes hostType)
            return null;

        try
        {
            using var compoundStructure = hostType.GetCompoundStructure();
            if (compoundStructure is null)
                return null;

            var layers = compoundStructure.GetLayers();
            var variableLayerIndex = compoundStructure.VariableLayerIndex;
            var exteriorShells = compoundStructure.GetNumberOfShellLayers(ShellLayerType.Exterior);
            var interiorShells = compoundStructure.GetNumberOfShellLayers(ShellLayerType.Interior);
            var snapshots = new List<CompoundLayerSnapshot>(layers.Count);

            for (var i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                string? materialName = null;
                try
                {
                    if (layer.MaterialId is not null && layer.MaterialId != ElementId.InvalidElementId)
                    {
                        materialName = doc.GetElement(layer.MaterialId)?.Name;
                    }
                }
                catch
                {
                    materialName = null;
                }

                // Wrapping participation is defined only for shell layers
                // (leading exterior + trailing interior ones).
                var isShellLayer = i < exteriorShells || i >= layers.Count - interiorShells;
                var participatesInWrapping = false;
                if (isShellLayer)
                {
                    try { participatesInWrapping = compoundStructure.ParticipatesInWrapping(i); }
                    catch { /* core-layer guard — stays false */ }
                }

                var layerCapFlag = false;
                try { layerCapFlag = layer.LayerCapFlag; }
                catch { /* best-effort flag */ }

                snapshots.Add(new CompoundLayerSnapshot(
                    Function: (int)layer.Function,
                    Width: layer.Width,
                    MaterialName: materialName,
                    IsVariable: i == variableLayerIndex,
                    LayerCapFlag: layerCapFlag,
                    ParticipatesInWrapping: participatesInWrapping));
            }

            var endCap = -1;
            var openingWrapping = -1;
            try { endCap = (int)compoundStructure.EndCap; } catch { /* stays unknown */ }
            try { openingWrapping = (int)compoundStructure.OpeningWrapping; } catch { /* stays unknown */ }

            return new CompoundStructureSnapshot(
                ExteriorShellLayerCount: exteriorShells,
                InteriorShellLayerCount: interiorShells,
                Layers: snapshots,
                StructuralMaterialIndex: compoundStructure.StructuralMaterialIndex,
                EndCap: endCap,
                OpeningWrapping: openingWrapping);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"CompoundStructure read failed for type '{elementType.Name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Routing preferences of a MEP curve type — pipe, duct, cable tray,
    /// conduit (ADR-056). <c>null</c> for non-MEP types. Rules keep
    /// manager order (first matching rule wins — order is content).
    /// Resolved part names are family-qualified for fitting symbols
    /// (<c>"{Family}:{Type}"</c>) so two fittings sharing a type name
    /// cannot collide.
    /// </summary>
    private static RoutingPreferencesSnapshot? ExtractRoutingPreferences(
        ElementType elementType, Document doc)
    {
        if (elementType is not MEPCurveType mepCurveType)
            return null;

        try
        {
            using var manager = mepCurveType.RoutingPreferenceManager;
            if (manager is null)
                return null;

            var rules = new List<RoutingRuleSnapshot>();
            foreach (RoutingPreferenceRuleGroupType group in Enum.GetValues(typeof(RoutingPreferenceRuleGroupType)))
            {
                if (group == RoutingPreferenceRuleGroupType.Undefined)
                    continue;

                int ruleCount = manager.GetNumberOfRules(group);
                for (var i = 0; i < ruleCount; i++)
                {
                    RoutingPreferenceRule rule;
                    try
                    {
                        rule = manager.GetRule(group, i);
                    }
                    catch (Exception ex)
                    {
                        SmartConLogger.Debug(
                            $"Routing rule read failed ({group}[{i}]) for type '{elementType.Name}': {ex.Message}");
                        continue;
                    }

                    rules.Add(ConvertRoutingRule(rule, group, doc));
                }
            }

            return new RoutingPreferencesSnapshot(
                PreferredJunctionType: (int)manager.PreferredJunctionType,
                Rules: rules);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"RoutingPreferences read failed for type '{elementType.Name}': {ex.Message}");
            return null;
        }
    }

    private static RoutingRuleSnapshot ConvertRoutingRule(
        RoutingPreferenceRule rule, RoutingPreferenceRuleGroupType group, Document doc)
    {
        string? partName = null;
        try
        {
            if (rule.MEPPartId is not null && rule.MEPPartId != ElementId.InvalidElementId)
            {
                var element = doc.GetElement(rule.MEPPartId);
                partName = element switch
                {
                    FamilySymbol symbol => $"{symbol.Family?.Name}:{symbol.Name}",
                    _ => element?.Name,
                };
            }
        }
        catch
        {
            partName = null;
        }

        var criteria = new List<RoutingCriterionSnapshot>();
        try
        {
            var criteriaCount = rule.NumberOfCriteria;
            for (var i = 0; i < criteriaCount; i++)
            {
                var criterion = rule.GetCriterion(i);
                switch (criterion)
                {
                    case PrimarySizeCriterion sizeCriterion:
                        criteria.Add(new RoutingCriterionSnapshot(
                            nameof(PrimarySizeCriterion),
                            sizeCriterion.MinimumSize,
                            sizeCriterion.MaximumSize));
                        break;
                    case not null:
                        criteria.Add(new RoutingCriterionSnapshot(
                            criterion.GetType().Name, 0, 0));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Routing criteria read failed: {ex.Message}");
        }

        string description;
        try
        {
            description = rule.Description ?? string.Empty;
        }
        catch
        {
            description = string.Empty;
        }

        return new RoutingRuleSnapshot(
            GroupType: (int)group,
            PartName: partName,
            Description: description,
            Criteria: criteria);
    }

    /// <summary>
    /// Segment size tables referenced by the type's routing rules
    /// (Segments group) — FHV4 hash content (#179, ADR-065). Mirrors
    /// <c>RevitSegmentSyncService.ReadSegment</c>; <c>null</c> for
    /// non-MEP types and types without segment rules.
    /// </summary>
    private static IReadOnlyList<SegmentSnapshot>? ExtractSegments(
        ElementType elementType, Document doc)
    {
        if (elementType is not MEPCurveType mepCurveType)
            return null;

        try
        {
            using var manager = mepCurveType.RoutingPreferenceManager;
            if (manager is null)
                return null;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var segments = new List<SegmentSnapshot>();
            var ruleCount = manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments);
            for (var i = 0; i < ruleCount; i++)
            {
                RoutingPreferenceRule rule;
                try
                {
                    rule = manager.GetRule(RoutingPreferenceRuleGroupType.Segments, i);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug(
                        $"Segment rule read failed (Segments[{i}]) for type '{elementType.Name}': {ex.Message}");
                    continue;
                }

                if (rule.MEPPartId is null || rule.MEPPartId == ElementId.InvalidElementId)
                    continue;
                if (doc.GetElement(rule.MEPPartId) is not Segment segment)
                    continue;
                if (!seen.Add(segment.Name))
                    continue;

                segments.Add(BuildSegmentSnapshot(segment, doc));
            }

            return segments.Count == 0 ? null : segments;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Segments read failed for type '{elementType.Name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Shared segment snapshot builder (FHV4, ADR-065) — single source for
    /// the hash path (extractor) and the sync path
    /// (<c>RevitSegmentSyncService.ReadSegment</c>), so the hash always
    /// reflects exactly what the sync writes.
    /// </summary>
    internal static SegmentSnapshot BuildSegmentSnapshot(Segment segment, Document doc)
    {
        string? materialName = null;
        try
        {
            if (segment.MaterialId is not null && segment.MaterialId != ElementId.InvalidElementId)
            {
                materialName = doc.GetElement(segment.MaterialId)?.Name;
            }
        }
        catch { /* unresolved material — null */ }

        string? scheduleName = null;
        if (segment is PipeSegment pipeSegment)
        {
            try
            {
                if (pipeSegment.ScheduleTypeId is not null &&
                    pipeSegment.ScheduleTypeId != ElementId.InvalidElementId)
                {
                    scheduleName = doc.GetElement(pipeSegment.ScheduleTypeId)?.Name;
                }
            }
            catch { /* unresolved schedule — null */ }
        }

        var sizes = segment.GetSizes()
            .Select(s => new SegmentSizeSnapshot(
                s.NominalDiameter, s.InnerDiameter, s.OuterDiameter,
                s.UsedInSizeLists, s.UsedInSizing))
            .ToList();

        return new SegmentSnapshot(segment.Name, materialName, scheduleName, segment.Roughness, sizes);
    }

    /// <summary>
    /// Stairs subtype references by NAME — FHV4 hash identity (#184,
    /// ADR-065). <c>null</c> for non-stairs types. The cut mark type has
    /// no dedicated property — it is read via the
    /// <c>STAIRSTYPE_CUTMARK_TYPE</c> built-in parameter (Autodesk
    /// Stairs Annotations guide).
    /// </summary>
    private static StairsSubtypesSnapshot? ExtractStairsSubtypes(
        ElementType elementType, Document doc)
    {
        if (elementType is not StairsType stairsType)
            return null;

        try
        {
            string? NameOf(ElementId? id)
            {
                try
                {
                    return id is null || id == ElementId.InvalidElementId
                        ? null
                        : doc.GetElement(id)?.Name;
                }
                catch
                {
                    return null;
                }
            }

            ElementId? cutMarkId = null;
            try
            {
                cutMarkId = stairsType
                    .get_Parameter(BuiltInParameter.STAIRSTYPE_CUTMARK_TYPE)
                    ?.AsElementId();
            }
            catch { /* no cut mark parameter — null */ }

            return new StairsSubtypesSnapshot(
                RunTypeName: NameOf(stairsType.RunType),
                LandingTypeName: NameOf(stairsType.LandingType),
                LeftSupportTypeName: NameOf(stairsType.LeftSideSupportType),
                RightSupportTypeName: NameOf(stairsType.RightSideSupportType),
                MiddleSupportTypeName: NameOf(stairsType.MiddleSupportType),
                CutMarkTypeName: NameOf(cutMarkId));
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Stairs subtypes read failed for type '{elementType.Name}': {ex.Message}");
            return null;
        }
    }

    private static bool IsValidId(ElementId? id) =>
        id is not null && id != ElementId.InvalidElementId;

    /// <summary>
    /// FHV5: wire settings identity summary — the material/temperature
    /// rating/insulation/max-size/conduit references of a <see cref="WireType"/>
    /// plus the neutral scalars. These are API properties backed by the
    /// <c>ElectricalSetting</c> object graph, NOT element parameters, so the
    /// generic pipeline never sees them (manual test 2026-08-04: a material
    /// change on a wire type did not sync). Revit 2026 replaced this object
    /// graph with the Conductor* element model (WireType.WireMaterial/
    /// TemperatureRating/Insulation are ElementId, MaxSize is string) — the
    /// conductor identity read is not ported yet, so the wire section is
    /// omitted from the hash on 2026+ (wire types hash without it there).
    /// Conductor* port: #233.
    /// </summary>
    private static WireSettingsSnapshot? ExtractWireSettings(ElementType elementType)
    {
#if REVIT2026_OR_GREATER
        return null;
#else
        if (elementType is not WireType wireType)
            return null;

        try
        {
            return new WireSettingsSnapshot(
                MaterialName: wireType.WireMaterial?.Name,
                TemperatureRatingName: wireType.TemperatureRating?.Name,
                InsulationName: wireType.Insulation?.Name,
                MaxSizeName: wireType.MaxSize?.Size,
                ConduitName: wireType.Conduit?.Name,
                NeutralMultiplier: wireType.NeutralMultiplier,
                NeutralRequired: wireType.NeutralRequired);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Wire settings read failed for type '{elementType.Name}': {ex.Message}");
            return null;
        }
#endif
    }

    /// <summary>
    /// Railing structure identity summary — FHV4 hash content (ADR-065):
    /// top rail, handrails, the non-continuous rail list and the baluster
    /// placement scalars. Element references are carried by NAME (user
    /// content, locale-stable); baluster families are family-qualified
    /// ("{Family}:{Type}") like routing part names. <c>null</c> for
    /// non-railing types.
    /// </summary>
    private static RailingStructureSnapshot? ExtractRailingStructure(
        ElementType elementType, Document doc)
    {
        if (elementType is not RailingType railingType)
            return null;

        try
        {
            string? NameOf(ElementId? id)
            {
                try
                {
                    return id is null || id == ElementId.InvalidElementId
                        ? null
                        : doc.GetElement(id)?.Name;
                }
                catch
                {
                    return null;
                }
            }

            string? FamilyQualifiedNameOf(ElementId? id)
            {
                try
                {
                    if (id is null || id == ElementId.InvalidElementId)
                        return null;
                    return doc.GetElement(id) switch
                    {
                        FamilySymbol symbol => $"{symbol.Family?.Name}:{symbol.Name}",
                        var element => element?.Name,
                    };
                }
                catch
                {
                    return null;
                }
            }

            var rails = new List<RailingRailSnapshot>();
            using (var railStructure = railingType.RailStructure)
            {
                if (railStructure is not null)
                {
                    var railCount = railStructure.GetNonContinuousRailCount();
                    for (var i = 0; i < railCount; i++)
                    {
                        using var rail = railStructure.GetNonContinuousRail(i);
                        if (rail is null) continue;
                        rails.Add(new RailingRailSnapshot(
                            rail.Name ?? string.Empty,
                            rail.Height,
                            rail.Offset,
                            FamilyQualifiedNameOf(rail.ProfileId),
                            NameOf(rail.MaterialId)));
                    }
                }
            }

            RailingBalusterSnapshot balusters;
            using (var placement = railingType.BalusterPlacement)
            {
                if (placement is not null)
                {
                    var balusterNames = new List<string?>();
                    double patternLength = 0;
                    var justification = -1;
                    var breakPattern = -1;
                    using (var pattern = placement.BalusterPattern)
                    {
                        if (pattern is not null)
                        {
                            patternLength = pattern.Length;
                            justification = (int)pattern.DistributionJustification;
                            breakPattern = (int)pattern.BreakPattern;
                            var balusterCount = pattern.GetBalusterCount();
                            for (var i = 0; i < balusterCount; i++)
                            {
                                using var baluster = pattern.GetBaluster(i);
                                balusterNames.Add(baluster is null
                                    ? null
                                    : FamilyQualifiedNameOf(baluster.BalusterFamilyId));
                            }
                        }
                    }

                    balusters = new RailingBalusterSnapshot(
                        PatternLength: patternLength,
                        DistributionJustification: justification,
                        BreakPattern: breakPattern,
                        BalusterFamilyNames: balusterNames,
                        UseBalusterPerTreadOnStairs: placement.UseBalusterPerTreadOnStairs,
                        BalusterPerTreadNumber: placement.BalusterPerTreadNumber,
                        BalusterPerTreadFamilyName: FamilyQualifiedNameOf(placement.BalusterPerTreadFamilyId));
                }
                else
                {
                    balusters = new RailingBalusterSnapshot(
                        0, -1, -1, [], false, 0, null);
                }
            }

            // Handrail-less railings: the handrail height/offset/position
            // getters THROW "The rail has no primary/secondary hand rail"
            // (stress test 2026-08-04 — the whole structure read aborted and
            // the RAILING hash section was lost). Guard each group by the
            // handrail reference; null = no handrail (deterministic state).
            var hasPrimary = IsValidId(railingType.PrimaryHandrailType);
            var hasSecondary = IsValidId(railingType.SecondaryHandrailType);

            return new RailingStructureSnapshot(
                TopRailTypeName: NameOf(railingType.TopRailType),
                TopRailHeight: railingType.TopRailHeight,
                PrimaryHandrailTypeName: NameOf(railingType.PrimaryHandrailType),
                PrimaryHandrailHeight: hasPrimary ? railingType.PrimaryHandrailHeight : null,
                PrimaryHandrailLateralOffset: hasPrimary ? railingType.PrimaryHandrailLateralOffset : null,
                PrimaryHandrailPosition: hasPrimary ? (int)railingType.PrimaryHandRailPosition : null,
                SecondaryHandrailTypeName: NameOf(railingType.SecondaryHandrailType),
                SecondaryHandrailHeight: hasSecondary ? railingType.SecondaryHandrailHeight : null,
                SecondaryHandrailLateralOffset: hasSecondary ? railingType.SecondaryHandrailLateralOffset : null,
                SecondaryHandrailPosition: hasSecondary ? (int)railingType.SecondaryHandRailPosition : null,
                Rails: rails,
                Balusters: balusters);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Railing structure read failed for type '{elementType.Name}': {ex.Message}");
            return null;
        }
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

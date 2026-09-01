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
    public FamilySnapshot ExtractFromFamilyDocument(
        Document familyDoc,
        IReadOnlyCollection<string>? preferredTypeNames = null)
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

        // FHV15 (#249, manual-test round 3): the type-DEPENDENT sections
        // (GEOM metrics, DEF offsets, CONN positions) are measured at a
        // deterministic reference type — the user's current-type choice in
        // the family editor is not a content change, and measuring at it
        // fired every evaluated section on a single-type value edit.
        var (geometry, definitions, connectors, behaviorFlags) =
            ExtractEvaluatedAtReferenceType(familyDoc, fm, preferredTypeNames);

        var (sharedNested, nonSharedNested) = ExtractNestedNames(familyDoc);
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
            LookupTables: lookupTables,
            Definitions: definitions);
    }

    /// <summary>
    /// FHV15 (#249, manual-test round 3): evaluates the type-DEPENDENT
    /// extraction steps (GEOM solid metrics + visibility flags + nested
    /// placements, DEF extrusion offsets, CONN positions, behavior flags)
    /// at a DETERMINISTIC reference type: the first (Ordinal) name of
    /// <paramref name="preferredTypeNames"/> intersected with the
    /// document's named types (the verifier's type-set rule — a partially
    /// loaded embedded copy compares against a restricted file snapshot),
    /// or the document's first named type by default. The switch runs in
    /// a transaction that is ROLLED BACK (I-03b precedent: Geo3DPerType) —
    /// the document keeps its state and IsModified flag; a SubTransaction
    /// is used when the caller already holds a transaction. Best-effort:
    /// a failed switch is logged and extraction proceeds at the current
    /// type (an honest mismatch beats a broken flow).
    /// </summary>
    private static (GeometryMetrics, DefinitionMetrics, List<ConnectorSnapshot>, FamilyBehaviorFlags?)
        ExtractEvaluatedAtReferenceType(
            Document familyDoc,
            Autodesk.Revit.DB.FamilyManager fm,
            IReadOnlyCollection<string>? preferredTypeNames)
    {
        (GeometryMetrics, DefinitionMetrics, List<ConnectorSnapshot>, FamilyBehaviorFlags?) Extract()
            => (ExtractGeometry(familyDoc), ExtractDefinitions(familyDoc),
                ExtractConnectors(familyDoc), ExtractBehaviorFlags(familyDoc));

        var referenceType = ResolveReferenceType(fm, preferredTypeNames);
        if (referenceType is null
            || string.Equals(fm.CurrentType?.Name, referenceType.Name, StringComparison.Ordinal))
        {
            return Extract();
        }

        try
        {
            if (familyDoc.IsModifiable)
            {
                using var st = new SubTransaction(familyDoc);
                st.Start();
                try
                {
                    fm.CurrentType = referenceType;
                    return Extract();
                }
                finally
                {
                    st.RollBack();
                }
            }

            using var tx = new Transaction(familyDoc, "SmartCon_HashReferenceType");
            tx.Start();
            try
            {
                fm.CurrentType = referenceType;
                return Extract();
            }
            finally
            {
                tx.RollBack();
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Reference-type switch to '{referenceType.Name}' failed: {ex.Message} " +
                "[Action: снимок извлекается при текущем типе — хэш может ложно не совпасть; сообщите разработчикам]");
            return Extract();
        }
    }

    /// <summary>
    /// The deterministic reference type for evaluated extraction
    /// (FHV15): the first (Ordinal) name of <paramref name="preferredTypeNames"/>
    /// present in the document's named types, or the document's first
    /// named type. <c>null</c> for typeless families (no named types) —
    /// no switch happens then.
    /// </summary>
    private static FamilyType? ResolveReferenceType(
        Autodesk.Revit.DB.FamilyManager fm,
        IReadOnlyCollection<string>? preferredTypeNames)
    {
        var byName = new Dictionary<string, FamilyType>(StringComparer.Ordinal);
        foreach (FamilyType t in fm.Types)
        {
            if (!string.IsNullOrWhiteSpace(t.Name) && !byName.ContainsKey(t.Name))
            {
                byName[t.Name] = t;
            }
        }
        if (byName.Count == 0)
        {
            return null;
        }

        var targetName = preferredTypeNames is not null
            ? preferredTypeNames
                .Where(byName.ContainsKey)
                .OrderBy(n => n, StringComparer.Ordinal)
                .FirstOrDefault()
            : null;
        targetName ??= byName.Keys.OrderBy(n => n, StringComparer.Ordinal).First();
        return byName[targetName];
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

            var extraction = RevitFamilyGeometryExtractor.ExtractMeshesFromFamilyDoc(familyDoc, ct);
            result.Add(new FamilyGeometryPerType(
                typeName, familyName, extraction.Meshes,
                new PreviewTypeSnapshot(typeName, extraction.PreviewForms, extraction.PreviewNestedInstances)));
            SmartConLogger.Info(
                $"ExtractGeometryPerType: single type '{typeName}' → {extraction.Meshes.Count} meshes, " +
                $"{(extraction.Meshes.Count > 0 ? extraction.Meshes.Sum(m => m.TriangleCount) : 0)} triangles");
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

                    var extraction = RevitFamilyGeometryExtractor.ExtractMeshesFromFamilyDoc(familyDoc, ct);

                    if (extraction.Meshes.Count > 0 && !extraction.Meshes.All(m => m.IsEmpty))
                    {
                        var triCount = extraction.Meshes.Sum(m => m.TriangleCount);
                        result.Add(new FamilyGeometryPerType(
                            ft.Name, familyName, extraction.Meshes,
                            new PreviewTypeSnapshot(ft.Name, extraction.PreviewForms, extraction.PreviewNestedInstances)));
                        SmartConLogger.Info(
                            $"ExtractGeometryPerType: type '{ft.Name}' → {extraction.Meshes.Count} meshes, {triCount} triangles");
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
            // A null fact = the computed value is unavailable this run —
            // OMIT it so the actualization detection stays pending and
            // self-heals (a sentinel would permanently clear the detection).
            var fact = ReadFact(familyDoc, rule);
            if (fact is not null)
            {
                result.Add(fact);
            }
        }
        return result;
    }

    private static FamilyFact ReadFact(Document familyDoc, FamilyFactRule rule)
    {
        // Computed facts (no backing parameter) have their own source —
        // the connector-shape mask comes from the family's ConnectorElements
        // (owner stress test 2026-09-01, баг 8: the routing picker filters
        // flex-duct candidates by connector profile).
        if (rule.ParameterId == FamilyFactRuleSet.ComputedFactParameterId)
        {
            return string.Equals(rule.FactKey, FamilyFactRuleSet.ConnectorShapeFactKey, StringComparison.Ordinal)
                ? ReadConnectorShapeFact(familyDoc, rule)
                : new FamilyFact(rule.FactKey, string.Empty, string.Empty);
        }

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

    /// <summary>
    /// Connector-shape bitmask of the family (Round=1, Rectangular=2,
    /// Oval=4 — a multi-shape transition like oval-round reports BOTH bits,
    /// so the picker matches it on either end). A family genuinely without
    /// connectors reports mask 0 (never matches a shape-filtered picker —
    /// correct: it cannot serve routing); a collector failure OMITS the
    /// fact entirely so the actualization detection stays pending and
    /// self-heals instead of permanently hiding the family from pickers.
    /// </summary>
    private static FamilyFact ReadConnectorShapeFact(Document familyDoc, FamilyFactRule rule)
    {
        var mask = 0;
        var names = new List<string>();
        try
        {
            var connectors = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(ConnectorElement))
                .Cast<ConnectorElement>();
            foreach (var connector in connectors)
            {
                switch (connector.Shape)
                {
                    case ConnectorProfileType.Round:
                        mask |= 1;
                        if (!names.Contains("Round")) names.Add("Round");
                        break;
                    case ConnectorProfileType.Rectangular:
                        mask |= 2;
                        if (!names.Contains("Rectangular")) names.Add("Rectangular");
                        break;
                    case ConnectorProfileType.Oval:
                        mask |= 4;
                        if (!names.Contains("Oval")) names.Add("Oval");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"ExtractFacts: connector-shape read failed in '{familyDoc.Title}': {ex.Message} — fact omitted (detection stays pending)");
            return null!;
        }

        return new FamilyFact(
            rule.FactKey,
            mask.ToString(CultureInfo.InvariantCulture),
            names.Count > 0 ? string.Join("+", names) : string.Empty);
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

            // FHV13/14 (#249 follow-up): curves dependent on a form (its
            // sketch content) are the parametric skeleton of 3D forms —
            // already measured by the GEOM metrics — so GEOM2D counts only
            // FREE 2D content. Without the exclusion, every "added a 3D
            // body" edit fired the 2D section (sketch curves + Revit's
            // automatic sketch dimensions).
            var sketchOwnedIds = CollectFormOwnedCurveIds(forms);

            var (symbolicCount, symbolicLength) = CountAndMeasureCurves(familyDoc,
                new CurveElementFilter(CurveElementType.SymbolicCurve), sketchOwnedIds);
            var (detailCount, detailLength) = CountAndMeasureCurves(familyDoc,
                new CurveElementFilter(CurveElementType.DetailCurve), sketchOwnedIds);
            var (modelCount, modelLength) = CountAndMeasureCurves(familyDoc,
                new CurveElementFilter(CurveElementType.ModelCurve), sketchOwnedIds);
            var textNoteCount = CountElements(familyDoc, typeof(TextNote));
            var refPlaneCount = CountElements(familyDoc, typeof(ReferencePlane));
            var dimensionCount = CountLabeledDimensions(familyDoc, sketchOwnedIds);

            // FHV12 (#249, Phase 3): nested instance placements are content
            // even in a form-less family (a pure container family).
            var nestedInstances = ExtractNestedInstances(familyDoc);

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
                    symbolicLength, detailLength, modelLength,
                    nestedInstances.Count > 0 ? nestedInstances : null);
            }

            // FHV12 (#249, Phase 3): IncludeNonVisibleObjects = true —
            // conditionally visible solids (IS_VISIBLE_PARAM = 0 on the
            // current type) enter the metrics WITH their visibility flag
            // recorded, closing the blind spot of conditional forms
            // invisible on the default type. Aligned with the GLB
            // extractor's options. NOTE: forms are NOT filtered by
            // form.Visible — probe 2026-08-27 showed form.Visible tracks
            // IS_VISIBLE_PARAM (both are the same "Visible" flag), so
            // filtering would drop exactly the conditional forms this
            // change exists to capture. FHV11 already counted invisible
            // forms (with zeroed metrics); FHV12 measures them.
            var options = new Options
            {
                ComputeReferences = false,
                DetailLevel = ViewDetailLevel.Fine,
                IncludeNonVisibleObjects = true
            };

            var metricsList = new List<FormMetrics>(forms.Count);

            foreach (var form in forms)
            {
                var metric = ExtractFormMetrics(form, options, familyDoc);
                metricsList.Add(metric);
            }

            var sortedMetrics = metricsList
                .OrderBy(f => f.FormKind, StringComparer.Ordinal)
                .ThenBy(f => f.IsSolid)
                .ThenBy(f => f.Volume)
                .ToList();

            SmartConLogger.Debug(
                $"Geometry: {forms.Count} forms ({metricsList.Count} visible in editor), " +
                $"{sortedMetrics.Count(f => f.IsSolid)} solid, " +
                $"{sortedMetrics.Count(f => !f.IsSolid)} void, " +
                $"{nestedInstances.Count} nested instances. " +
                $"2D: symbolic={symbolicCount}, detail={detailCount}, model={modelCount}, " +
                $"text={textNoteCount}, refPlane={refPlaneCount}, dim={dimensionCount}");

            return new GeometryMetrics(
                forms.Count, sortedMetrics,
                symbolicCount, detailCount, modelCount,
                textNoteCount, refPlaneCount, dimensionCount,
                symbolicLength, detailLength, modelLength,
                nestedInstances.Count > 0 ? nestedInstances : null);
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
    /// FHV12 (#249, Phase 3): nested <c>FamilyInstance</c> placements —
    /// symbol identity + transform + visibility flag. Pre-FHV12 only the
    /// nested family names participated in the hash (NESTED section):
    /// moving or rotating a nested part passed silently.
    /// </summary>
    private static List<NestedInstanceSnapshot> ExtractNestedInstances(Document familyDoc)
    {
        var result = new List<NestedInstanceSnapshot>();
        try
        {
            var collector = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(FamilyInstance));
            foreach (FamilyInstance fi in collector)
            {
                try
                {
                    var familyName = fi.Symbol?.Family?.Name;
                    if (string.IsNullOrEmpty(familyName))
                    {
                        continue;
                    }
                    var symbolName = fi.Symbol?.Name ?? string.Empty;
                    var transform = fi.GetTransform();
                    int? isVisibleParam = null;
                    try
                    {
                        isVisibleParam = fi.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM)?.AsInteger();
                    }
                    catch
                    {
                        // flag unreadable — null recorded
                    }

                    result.Add(new NestedInstanceSnapshot(
                        familyName!, symbolName,
                        transform.Origin.X, transform.Origin.Y, transform.Origin.Z,
                        transform.BasisX.X, transform.BasisX.Y, transform.BasisX.Z,
                        transform.BasisY.X, transform.BasisY.Y, transform.BasisY.Z,
                        transform.BasisZ.X, transform.BasisZ.Y, transform.BasisZ.Z,
                        isVisibleParam));
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug($"Nested instance read failed (Id={fi.Id}): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Nested instance collection failed: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// FHV12 (#249, Phase 3): DEF — the type-independent definition
    /// wiring of the family: which FAMILY parameters drive each form's
    /// visibility/material/extrusion offsets, each dimension's label and
    /// each reference plane's identity. One collector pass per element
    /// class, zero regenerations. Bindings are read via
    /// <c>FamilyManager.GetAssociatedFamilyParameter</c> — the same API
    /// <c>AssociateElementParameterToFamilyParameter</c> writes through,
    /// so a re-binding to a different parameter with identical current
    /// values is caught here (the pre-FHV12 blind spot).
    /// </summary>
    private static DefinitionMetrics ExtractDefinitions(Document familyDoc)
    {
        var fm = familyDoc.FamilyManager;
        var forms = new List<FormDefinitionSnapshot>();
        var dimensions = new List<DimensionDefinitionSnapshot>();
        var planes = new List<ReferencePlaneDefinitionSnapshot>();

        try
        {
            foreach (var form in new FilteredElementCollector(familyDoc)
                .OfClass(typeof(GenericForm)).Cast<GenericForm>())
            {
                string? subcategory;
                try
                {
                    subcategory = form.Subcategory?.Name;
                }
                catch
                {
                    subcategory = null;
                }

                double? startOffset = null;
                double? endOffset = null;
                string? startBinding = null;
                string? endBinding = null;
                if (form is Extrusion extrusion)
                {
                    startBinding = GetParameterBindingName(fm, form, BuiltInParameter.EXTRUSION_START_PARAM);
                    endBinding = GetParameterBindingName(fm, form, BuiltInParameter.EXTRUSION_END_PARAM);
                    try
                    {
                        startOffset = extrusion.StartOffset;
                        endOffset = extrusion.EndOffset;
                    }
                    catch
                    {
                        // offsets unreadable — null recorded
                    }
                }

                forms.Add(new FormDefinitionSnapshot(
                    form.GetType().Name,
                    form.IsSolid,
                    subcategory,
                    GetParameterBindingName(fm, form, BuiltInParameter.IS_VISIBLE_PARAM),
                    GetParameterBindingName(fm, form, BuiltInParameter.MATERIAL_ID_PARAM),
                    startBinding,
                    endBinding,
                    startOffset,
                    endOffset));
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Definition extraction (forms) failed: {ex.Message}");
        }

        try
        {
            foreach (var dim in new FilteredElementCollector(familyDoc)
                .OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                string? label = null;
                try
                {
                    label = dim.FamilyLabel?.Definition?.Name;
                }
                catch
                {
                    // unlabeled or unreadable — null recorded
                }

                string styleName;
                try
                {
                    styleName = familyDoc.GetElement(dim.GetTypeId())?.Name ?? string.Empty;
                }
                catch
                {
                    styleName = string.Empty;
                }

                var segmentCount = 0;
                try
                {
                    segmentCount = dim.Segments?.Size ?? 0;
                }
                catch
                {
                    // segments unreadable — 0 recorded
                }

                dimensions.Add(new DimensionDefinitionSnapshot(label, styleName, segmentCount));
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Definition extraction (dimensions) failed: {ex.Message}");
        }

        try
        {
            foreach (var plane in new FilteredElementCollector(familyDoc)
                .OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
            {
                string name;
                try
                {
                    // ReferencePlane.Name throws for unnamed planes — fall
                    // back to the generic element name, then to empty.
                    name = plane.Name ?? string.Empty;
                }
                catch
                {
                    name = string.Empty;
                }

                bool? definesOrigin = null;
                try
                {
                    var value = plane.get_Parameter(BuiltInParameter.DATUM_PLANE_DEFINES_ORIGIN)?.AsInteger();
                    if (value.HasValue)
                    {
                        definesOrigin = value.Value != 0;
                    }
                }
                catch
                {
                    // flag unreadable — null recorded
                }

                planes.Add(new ReferencePlaneDefinitionSnapshot(name, definesOrigin));
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Definition extraction (reference planes) failed: {ex.Message}");
        }

        return new DefinitionMetrics(forms, dimensions, planes);
    }

    /// <summary>
    /// Name of the FAMILY parameter associated with the element's
    /// built-in parameter, or <c>null</c> when the parameter is missing
    /// or not associated. Best-effort:
    /// <c>GetAssociatedFamilyParameter</c> may reject non-associable
    /// parameters — the binding then records null.
    /// </summary>
    private static string? GetParameterBindingName(Autodesk.Revit.DB.FamilyManager fm, Element element, BuiltInParameter bip)
    {
        try
        {
            var parameter = element.get_Parameter(bip);
            if (parameter is null)
            {
                return null;
            }
            return fm.GetAssociatedFamilyParameter(parameter)?.Definition?.Name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// FHV14 (#249 follow-up): element ids of curve elements DEPENDENT on a
    /// form — i.e. its sketch's model curves — via
    /// <c>Element.GetDependentElements</c> (Revit 2018+, every supported
    /// version; <c>Sketch.OwnerId</c>/<c>GetAllElements</c> are 2024+ and
    /// failed the net48 build). Dependent = "deleted together with the
    /// form", so free model/symbolic lines — which probe 2026-08-28 showed
    /// wrapped in their OWN sketches with no form owner — are never
    /// matched and stay counted. Best-effort per form: an unreadable form
    /// keeps its curves counted (fail-open, pre-FHV13 behaviour).
    /// </summary>
    private static HashSet<ElementId> CollectFormOwnedCurveIds(IReadOnlyList<GenericForm> forms)
    {
        var ids = new HashSet<ElementId>();
        try
        {
            var classFilter = new ElementClassFilter(typeof(CurveElement));
            foreach (var form in forms)
            {
                try
                {
                    foreach (var id in form.GetDependentElements(classFilter))
                    {
                        ids.Add(id);
                    }
                }
                catch
                {
                    // single form unreadable — its curves stay counted
                }
            }
        }
        catch
        {
            // dependency query unsupported — fall back to counting everything
        }
        return ids;
    }

    /// <summary>
    /// Count curve elements matching the filter and sum their geometry
    /// curve lengths (ADR-056). Length catches 2D edits that keep the
    /// element count constant (redrawn line of the same kind). Elements
    /// owned by form sketches (FHV13) are excluded — they are 3D-form
    /// wiring, measured by the GEOM metrics.
    /// </summary>
    private static (int Count, double TotalLength) CountAndMeasureCurves(
        Document doc, ElementFilter filter, ISet<ElementId>? excludeIds = null)
    {
        try
        {
            var count = 0;
            double length = 0;
            foreach (var element in new FilteredElementCollector(doc).WherePasses(filter))
            {
                if (excludeIds is not null && excludeIds.Contains(element.Id))
                {
                    continue;
                }
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

    /// <summary>
    /// FHV13 (#249 follow-up): number of LABELED dimensions not owned by a
    /// form sketch. Unlabeled dimensions — including Revit's automatic
    /// sketch dimensions, which even API-created extrusions leave behind —
    /// are not parameter wiring: their geometric effect is measured by the
    /// GEOM metrics, and counting them fired GEOM2D on every 3D edit.
    /// </summary>
    private static int CountLabeledDimensions(Document doc, ISet<ElementId> excludeIds)
    {
        var count = 0;
        try
        {
            foreach (var dim in new FilteredElementCollector(doc)
                .OfClass(typeof(Dimension))
                .Cast<Dimension>())
            {
                if (excludeIds.Contains(dim.Id))
                {
                    continue;
                }
                bool isLabeled;
                try { isLabeled = dim.FamilyLabel is not null; }
                catch { isLabeled = false; }
                if (isLabeled)
                {
                    count++;
                }
            }
        }
        catch
        {
            // partial count stands
        }
        return count;
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

    private static int CountElements(Document doc, Type elementType, ISet<ElementId>? excludeIds = null)
    {
        try
        {
            var count = 0;
            foreach (var element in new FilteredElementCollector(doc).OfClass(elementType))
            {
                if (excludeIds is not null && excludeIds.Contains(element.Id))
                {
                    continue;
                }
                count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    internal static FormMetrics ExtractFormMetrics(GenericForm form, Options options, Document familyDoc)
    {
        var formKind = form.GetType().Name;
        var isSolid = form.IsSolid;
        double volume = 0;
        double surfaceArea = 0;
        int faceCount = 0;
        int edgeCount = 0;
        double totalEdgeLength = 0;
        double centroidX = 0, centroidY = 0, centroidZ = 0;
        var faceTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        string? subcategoryName = null;
        BoundingBoxSnapshot? bounds = null;

        // FHV18 (#251): per-face resolved-color histogram inputs — the
        // form-level fallback color resolved ONCE (the same chain the GLB
        // side uses), plus a per-material color cache so a 1000-face form
        // costs one lookup per material, not per face.
        var formColor = ResolveFormMaterialColor(form, familyDoc);
        var faceColors = new Dictionary<MaterialColorSnapshot, int>();
        var faceColorCache = new Dictionary<ElementId, MaterialColorSnapshot?>();

        try
        {
            var geomElem = form.get_Geometry(options);
            if (geomElem is not null)
            {
                foreach (var geomObj in geomElem)
                {
                    if (geomObj is Solid solid && solid.Volume > 0)
                    {
                        AccumulateSolid(solid, ref volume, ref surfaceArea, ref faceCount, ref edgeCount,
                            ref totalEdgeLength, ref centroidX, ref centroidY, ref centroidZ, faceTypes,
                            familyDoc, formColor, faceColors, faceColorCache);
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
                                    AccumulateSolid(innerSolid, ref volume, ref surfaceArea, ref faceCount, ref edgeCount,
                                        ref totalEdgeLength, ref centroidX, ref centroidY, ref centroidZ, faceTypes,
                                        familyDoc, formColor, faceColors, faceColorCache);
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

        // FHV12 (#249, Phase 3): volume-weighted centroid — null when the
        // form produced no measurable solid (a deterministic state).
        PointSnapshot? centroid = volume > 0
            ? new PointSnapshot(centroidX / volume, centroidY / volume, centroidZ / volume)
            : null;

        return new FormMetrics(
            FormKind: formKind,
            IsSolid: isSolid,
            Volume: volume,
            FaceCount: faceCount,
            EdgeCount: edgeCount,
            SubcategoryName: subcategoryName,
            SurfaceArea: surfaceArea,
            Bounds: bounds,
            Centroid: centroid,
            FaceTypes: faceTypes.Count > 0
                ? faceTypes.Select(kv => new FaceTypeCount(kv.Key, kv.Value)).ToList()
                : null,
            TotalEdgeLength: totalEdgeLength,
            MaterialColor: formColor,
            Visibility: ExtractFormVisibility(form),
            FaceColors: faceColors.Count > 0
                ? faceColors.Select(kv => new FaceColorCount(kv.Key, kv.Value)).ToList()
                : null);
    }

    /// <summary>
    /// FHV12 (#249, Phase 3): resolved display color of a form through a
    /// fallback chain mirroring the GLB extractor
    /// (<c>RevitFamilyGeometryExtractor.GetColorForMaterialId</c>):
    /// form material parameter → element category material → owner family
    /// category material → <c>null</c> (deterministic "no color" state —
    /// the GLB side falls back to a constant grey, but for the HASH a
    /// null marker is the more honest, equally deterministic state).
    /// </summary>
    private static MaterialColorSnapshot? ResolveFormMaterialColor(GenericForm form, Document familyDoc)
    {
        try
        {
            var materialId = form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.AsElementId();
            var color = TryGetMaterialColorById(familyDoc, materialId);
            if (color is not null)
            {
                return color;
            }
        }
        catch
        {
            // fall through to the category chain
        }

        try
        {
            var color = TryGetMaterialColorById(familyDoc, form.Category?.Material?.Id);
            if (color is not null)
            {
                return color;
            }
        }
        catch
        {
            // fall through to the owner category
        }

        try
        {
            return TryGetMaterialColorById(familyDoc, familyDoc.OwnerFamily?.FamilyCategory?.Material?.Id);
        }
        catch
        {
            return null;
        }
    }

    private static MaterialColorSnapshot? TryGetMaterialColorById(Document doc, ElementId? materialId)
    {
        if (materialId is null)
        {
            return null;
        }
        try
        {
            if (doc.GetElement(materialId) is Material material)
            {
                var c = material.Color;
                if (c is not null && c.IsValid)
                {
                    return new MaterialColorSnapshot(c.Red, c.Green, c.Blue, 255);
                }
            }
        }
        catch
        {
            // unresolved level — caller walks the fallback chain
        }
        return null;
    }

    /// <summary>
    /// FHV12 (#249, Phase 3): visibility flags of a form — the raw
    /// <c>IS_VISIBLE_PARAM</c> value (the associable "Visible" parameter)
    /// and the Fine detail-level flag (<c>GenericForm.GetVisibility()</c>).
    /// Best-effort per flag: an unreadable flag records null.
    /// </summary>
    private static FormVisibilitySnapshot ExtractFormVisibility(GenericForm form)
    {
        int? isVisibleParam = null;
        try
        {
            isVisibleParam = form.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM)?.AsInteger();
        }
        catch
        {
            // flag unreadable — null recorded
        }

        bool? isShownInFine = null;
        try
        {
            isShownInFine = form.GetVisibility()?.IsShownInFine;
        }
        catch
        {
            // visibility object unavailable — null recorded
        }

        return new FormVisibilitySnapshot(isVisibleParam, isShownInFine);
    }

    /// <summary>
    /// #250: aggregate CONTENT metrics of a nested <see cref="FamilyInstance"/>'s
    /// SYMBOL geometry (placement-invariant — read from
    /// <c>GetSymbolGeometry()</c>) for the VIEW3D hash: a nested child's
    /// geometry/material edit must re-key the per-type CAS pool even when
    /// the placement (name + transform) is untouched. Best-effort:
    /// <c>null</c> on any read failure (the hasher emits a deterministic
    /// marker then). Face colors use the face material only — the nested
    /// child has no form-level fallback chain in the host context.
    /// </summary>
    internal static FormMetrics? ComputeNestedContentMetrics(
        Document familyDoc, FamilyInstance inst, Options options)
    {
        try
        {
            var geomElem = inst.get_Geometry(options);
            if (geomElem is null)
            {
                return null;
            }

            double volume = 0, surfaceArea = 0, totalEdgeLength = 0;
            double centroidX = 0, centroidY = 0, centroidZ = 0;
            int faceCount = 0, edgeCount = 0;
            var faceTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var faceColors = new Dictionary<MaterialColorSnapshot, int>();
            var faceColorCache = new Dictionary<ElementId, MaterialColorSnapshot?>();

            foreach (var geomObj in geomElem)
            {
                if (geomObj is GeometryInstance geomInst)
                {
                    var symbolGeom = geomInst.GetSymbolGeometry();
                    if (symbolGeom is null)
                    {
                        continue;
                    }
                    foreach (var innerObj in symbolGeom)
                    {
                        if (innerObj is Solid solid && solid.Volume > 0)
                        {
                            AccumulateSolid(solid, ref volume, ref surfaceArea, ref faceCount, ref edgeCount,
                                ref totalEdgeLength, ref centroidX, ref centroidY, ref centroidZ, faceTypes,
                                familyDoc, null, faceColors, faceColorCache);
                        }
                    }
                }
                else if (geomObj is Solid solid && solid.Volume > 0)
                {
                    AccumulateSolid(solid, ref volume, ref surfaceArea, ref faceCount, ref edgeCount,
                        ref totalEdgeLength, ref centroidX, ref centroidY, ref centroidZ, faceTypes,
                        familyDoc, null, faceColors, faceColorCache);
                }
            }

            return new FormMetrics(
                FormKind: "NestedContent",
                IsSolid: true,
                Volume: volume,
                FaceCount: faceCount,
                EdgeCount: edgeCount,
                SubcategoryName: null,
                SurfaceArea: surfaceArea,
                Bounds: null,
                Centroid: volume > 0
                    ? new PointSnapshot(centroidX / volume, centroidY / volume, centroidZ / volume)
                    : null,
                FaceTypes: faceTypes.Count > 0
                    ? faceTypes.Select(kv => new FaceTypeCount(kv.Key, kv.Value)).ToList()
                    : null,
                TotalEdgeLength: totalEdgeLength,
                MaterialColor: null,
                Visibility: null,
                FaceColors: faceColors.Count > 0
                    ? faceColors.Select(kv => new FaceColorCount(kv.Key, kv.Value)).ToList()
                    : null);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Nested content metrics failed for '{inst.Symbol?.Family?.Name}/{inst.Symbol?.Name}': {ex.Message}");
            return null;
        }
    }

    private static void AccumulateSolid(
        Solid solid, ref double volume, ref double surfaceArea, ref int faceCount, ref int edgeCount,
        ref double totalEdgeLength,
        ref double centroidX, ref double centroidY, ref double centroidZ,
        Dictionary<string, int> faceTypes,
        Document familyDoc,
        MaterialColorSnapshot? formColor,
        Dictionary<MaterialColorSnapshot, int> faceColors,
        Dictionary<ElementId, MaterialColorSnapshot?> faceColorCache)
    {
        volume += solid.Volume;
        faceCount += solid.Faces.Size;
        edgeCount += solid.Edges.Size;
        try
        {
            foreach (Face face in solid.Faces)
            {
                surfaceArea += face.Area;
                // FHV12: face-kind histogram — kinds are stable across
                // regenerations, unlike tessellation vertex counts.
                var kind = face.GetType().Name;
                faceTypes.TryGetValue(kind, out var count);
                faceTypes[kind] = count + 1;

                // FHV18 (#251): per-face resolved color — the face's own
                // material wins (a face PAINT overrides the form color in
                // the GLB too, #108); an own material without a resolvable
                // color and a face without any material land in the
                // form-level bucket. Faces with no color anywhere are not
                // counted (a deterministic state — FaceCount covers them).
                var bucket = formColor;
                var faceMaterialId = face.MaterialElementId;
                if (faceMaterialId is not null && faceMaterialId != ElementId.InvalidElementId)
                {
                    if (!faceColorCache.TryGetValue(faceMaterialId, out var faceColor))
                    {
                        faceColor = TryGetMaterialColorById(familyDoc, faceMaterialId);
                        faceColorCache[faceMaterialId] = faceColor;
                    }
                    if (faceColor is not null)
                    {
                        bucket = faceColor;
                    }
                }
                if (bucket is not null)
                {
                    faceColors.TryGetValue(bucket, out var colorCount);
                    faceColors[bucket] = colorCount + 1;
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Face area accumulation failed: {ex.Message}");
        }
        try
        {
            foreach (Edge edge in solid.Edges)
            {
                var curve = edge.AsCurve();
                if (curve is not null)
                {
                    totalEdgeLength += curve.Length;
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Edge length accumulation failed: {ex.Message}");
        }
        try
        {
            // FHV12: volume-weighted centroid accumulation.
            var c = solid.ComputeCentroid();
            centroidX += c.X * solid.Volume;
            centroidY += c.Y * solid.Volume;
            centroidZ += c.Z * solid.Volume;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Centroid accumulation failed: {ex.Message}");
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
            {
                // FHV19 (ADR-072): flex/conduit/cable-tray types have no
                // RoutingPreferenceManager (probe RoutingStorageReality
                // 2026-08-29) — their fitting selection lives in visible
                // built-in parameters.
                return ExtractParamBasedRouting(elementType, doc);
            }

            var rules = new List<RoutingRuleSnapshot>();
            // Only PIPES define size ranges in their routing rules (owner
            // decision 2026-08-30): duct manager rules still report a
            // default PrimarySizeCriterion, but duct size availability is
            // configured elsewhere — the criterion must not become routing
            // content (phantom size UI + drift against the editor, which
            // keeps no size conditions for non-pipes).
            var includeSizeCriteria = elementType is PipeType;
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

                    rules.Add(ConvertRoutingRule(rule, group, doc, includeSizeCriteria));
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

    /// <summary>
    /// Parameter-based routing of manager-less MEPCurve types — flex
    /// pipe/duct, conduit, cable tray (FHV19, ADR-072). Each visible
    /// routing-driving built-in parameter becomes one rule in a
    /// <c>"Param:&lt;BIP&gt;"</c> group (deterministic key order);
    /// <c>RBS_CURVETYPE_PREFERRED_BRANCH_PARAM</c> maps to
    /// <see cref="RoutingPreferencesSnapshot.PreferredJunctionType"/>.
    /// NOTE: the parameter's int convention is INVERTED against the
    /// <c>PreferredJunctionType</c> enum (param: 0=Tap, 1=Tee — Autodesk
    /// DevBlog; enum: Tee=0, Tap=1 — revitapidocs). The RAW value is stored
    /// so extract→DB→sync round-trips stably; the routing editor translates
    /// it per category for display (audit H3).
    /// <c>null</c> when the type exposes no routing-driving parameters at
    /// all (canonical "not routed" state).
    /// </summary>
    private static RoutingPreferencesSnapshot? ExtractParamBasedRouting(
        ElementType elementType, Document doc)
    {
        var keyed = new List<KeyValuePair<string, RoutingRuleSnapshot>>();
        var preferredJunction = 0;
        var foundAny = false;

        foreach (Parameter param in elementType.Parameters)
        {
            if (RoutingDrivingParameters.IsPreferredBranch(param))
            {
                foundAny = true;
                try
                {
                    if (param.HasValue)
                        preferredJunction = param.AsInteger();
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug(
                        $"PreferredBranch read failed for type '{elementType.Name}': {ex.Message}");
                }
                continue;
            }

            var bip = RoutingDrivingParameters.TryGetRoutingParam(param);
            if (bip is null)
                continue;
            foundAny = true;

            string? partName = null;
            try
            {
                if (param.HasValue)
                {
                    var partId = param.AsElementId();
                    if (partId is not null && partId != ElementId.InvalidElementId)
                    {
                        var element = doc.GetElement(partId);
                        // Audit L21: a non-invalid id resolving to NOTHING is
                        // a stale reference (deleted fitting), not a
                        // deliberate «Нет» — mask it and the catalog loses
                        // the distinction (and the presence flag) silently.
                        if (element is null)
                        {
                            SmartConLogger.Warn(
                                $"Param routing rule ({bip}) of type '{elementType.Name}' holds a stale " +
                                $"ElementId {partId} — recorded as «Нет». [Action: переназначьте деталь " +
                                "в свойствах типа в Revit или во вкладке «Трассировка»]");
                        }
                        partName = element switch
                        {
                            FamilySymbol symbol => $"{symbol.Family?.Name}:{symbol.Name}",
                            _ => element?.Name,
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug(
                    $"Param routing rule read failed ({bip}) for type '{elementType.Name}': {ex.Message}");
                partName = null;
            }

            var groupKey = RoutingDrivingParameters.GroupKey(bip.Value);
            keyed.Add(new KeyValuePair<string, RoutingRuleSnapshot>(
                groupKey,
                new RoutingRuleSnapshot(
                    RoutingGroupKeys.ParamGroupType,
                    partName,
                    string.Empty,
                    Array.Empty<RoutingCriterionSnapshot>(),
                    GroupKey: groupKey)));
        }

        if (!foundAny)
            return null;

        var rules = keyed
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .ToList();
        return new RoutingPreferencesSnapshot(preferredJunction, rules);
    }

    private static RoutingRuleSnapshot ConvertRoutingRule(
        RoutingPreferenceRule rule, RoutingPreferenceRuleGroupType group, Document doc, bool includeSizeCriteria)
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
                    case PrimarySizeCriterion sizeCriterion when includeSizeCriteria:
                        criteria.Add(new RoutingCriterionSnapshot(
                            nameof(PrimarySizeCriterion),
                            sizeCriterion.MinimumSize,
                            sizeCriterion.MaximumSize));
                        break;
                    case PrimarySizeCriterion:
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

                // FHV21 (owner decision 2026-09-01): the rule's size-range
                // criterion (Мин/Макс in the routing dialog) is part of the
                // segment configuration — it enters the SEGMENTS hash
                // section and the per-version segment-rule store.
                double? ruleMin = null;
                double? ruleMax = null;
                try
                {
                    for (var c = 0; c < rule.NumberOfCriteria; c++)
                    {
                        if (rule.GetCriterion(c) is PrimarySizeCriterion sizeCriterion)
                        {
                            ruleMin = sizeCriterion.MinimumSize;
                            ruleMax = sizeCriterion.MaximumSize;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug(
                        $"Segment rule criterion read failed for '{segment.Name}': {ex.Message}");
                }

                segments.Add(BuildSegmentSnapshot(segment, doc) with
                {
                    RuleMinSizeFeet = ruleMin,
                    RuleMaxSizeFeet = ruleMax,
                });
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

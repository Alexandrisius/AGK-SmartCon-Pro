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
public sealed partial class RevitFamilySnapshotExtractor : IFamilySnapshotExtractor
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

}

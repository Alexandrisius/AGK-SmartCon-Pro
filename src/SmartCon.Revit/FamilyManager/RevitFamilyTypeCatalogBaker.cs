using System.IO;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Compatibility;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Bakes a Type Catalog (.txt) into a Revit family (.rfa) by opening the
/// source .rfa, creating every catalog type inside the family document, and
/// saving the result directly to the requested output path. Temporarily
/// disables formulas that reference catalog input parameters, restores them
/// after all types are created, regenerates, and commits.
/// </summary>
public sealed partial class RevitFamilyTypeCatalogBaker : IFamilyTypeCatalogBaker
{
    private const string BakeTransactionName = "SmartCon_BakeTypeCatalog";
    private const string AnchorTypeName = "__SmartCon_BakeAnchor__";

    private readonly IFamilyManagerAwaitableEvent _awaitableEvent;
    private readonly ITransactionService _transactionService;
    private readonly ITypeCatalogValueApplier _valueApplier;
    private readonly IFormulaSolver _formulaSolver;

    public RevitFamilyTypeCatalogBaker(
        IFamilyManagerAwaitableEvent awaitableEvent,
        ITransactionService transactionService,
        ITypeCatalogValueApplier valueApplier,
        IFormulaSolver formulaSolver)
    {
        _awaitableEvent = awaitableEvent ?? throw new ArgumentNullException(nameof(awaitableEvent));
        _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
        _valueApplier = valueApplier ?? throw new ArgumentNullException(nameof(valueApplier));
        _formulaSolver = formulaSolver ?? throw new ArgumentNullException(nameof(formulaSolver));
    }

    public async Task<FamilyTypeCatalogBakingResult> BakeAsync(
        string sourceRfaPath,
        TypeCatalogParseResult catalog,
        string outputRfaPath,
        CancellationToken ct = default)
    {
        var sourceFileName = Path.GetFileName(sourceRfaPath);
        var outputFileName = Path.GetFileName(outputRfaPath);

        return await _awaitableEvent.RaiseAsync<FamilyTypeCatalogBakingResult>(obj =>
        {
            var uiApp = (Autodesk.Revit.UI.UIApplication)obj;
            var app = uiApp.Application;
            Document? familyDoc = null;

            SmartConLogger.FreezeThreadPool("Bake.beforeOpen");

            try
            {
                EnsureOutputDirectory(outputRfaPath);
                EnsureWritable(outputRfaPath);

                double openMs;
                using (var _openMs = SmartConLogger.Measure("Bake.OpenDocumentFile"))
                {
                    SmartConLogger.Freeze($"Bake: Starting OpenDocumentFile for '{sourceFileName}'");
                    familyDoc = app.OpenDocumentFile(sourceRfaPath);
                    openMs = _openMs.GetElapsedMilliseconds();
                    SmartConLogger.Freeze($"Bake: OpenDocumentFile completed in {openMs}ms");
                }
                if (familyDoc is null)
                {
                    return Failure($"Failed to open family document '{sourceRfaPath}'");
                }

                if (!familyDoc.IsFamilyDocument)
                {
                    return Failure($"File is not a family document: '{sourceRfaPath}'");
                }

                var bakeResult = BakeInFamilyDocument(familyDoc, catalog, ct);
                if (!bakeResult.Success)
                {
                    return bakeResult;
                }

                double saveMs;
                using (var _saveMs = SmartConLogger.Measure("Bake.SaveAs"))
                {
                    using var saveOptions = new SaveAsOptions { OverwriteExistingFile = true };
                    familyDoc.SaveAs(outputRfaPath, saveOptions);
                    SetReadOnly(outputRfaPath);
                    saveMs = _saveMs.GetElapsedMilliseconds();
                }
                SmartConLogger.Freeze($"Bake: SaveAs completed in {saveMs}ms");
                SmartConLogger.Info($"Baked family saved to '{outputFileName}': rows={catalog.Entries.Count}");

                return new FamilyTypeCatalogBakingResult(
                    Success: true,
                    OutputRfaPath: outputRfaPath,
                    BakedTypeCount: bakeResult.BakedTypeCount,
                    ErrorMessage: null);
            }
            catch (Exception ex)
            {
                SmartConLogger.FreezeFail("Bake", $"{ex.GetType().Name}: {ex.Message}");
                SmartConLogger.Warn(
                    $"Type Catalog bake failed for '{sourceFileName}': {ex.Message} " +
                    "[Action: verify the .rfa and .txt are compatible, or check the Revit journal for details]");
                return Failure(ex.Message);
            }
            finally
            {
                if (familyDoc != null)
                {
                    SmartConLogger.Freeze("Bake: Starting Close");
                    using (var _closeMs = SmartConLogger.Measure("Bake.Close"))
                    {
                        try { familyDoc.Close(false); }
                        catch (Exception ex)
                        {
                            var closeMs = _closeMs.GetElapsedMilliseconds();
                            SmartConLogger.FreezeFail("Bake.Close", $"after {closeMs}ms: {ex.GetType().Name}: {ex.Message}");
                        }
                        var closeMsFinal = _closeMs.GetElapsedMilliseconds();
                        SmartConLogger.Freeze($"Bake: Close completed in {closeMsFinal}ms");
                    }

                    try { Marshal.ReleaseComObject(familyDoc); } catch { /* ignore */ }
                }
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Phase 27B: bakes the Type Catalog into an already-open family document
    /// (held open by Prepare). Does NOT open, save, or close — the caller holds
    /// the document for the later Phase 3 SaveAs. Eliminates the re-open that
    /// <see cref="BakeAsync"/> performs.
    /// </summary>
    public async Task<FamilyTypeCatalogBakingResult> BakeInExistingDocumentAsync(
        object familyDoc,
        TypeCatalogParseResult catalog,
        CancellationToken ct = default)
    {
        if (familyDoc is null)
            return Failure("familyDoc is null");

        return await _awaitableEvent.RaiseAsync<FamilyTypeCatalogBakingResult>(obj =>
        {
            try
            {
                var doc = (Document)familyDoc;
                if (!doc.IsFamilyDocument)
                    return Failure("Document is not a family document");

                SmartConLogger.Info(
                    $"BakeInExistingDocument: baking {catalog.Entries.Count} type(s) " +
                    $"into held-open family document (no re-open, no save)");

                var bakeResult = BakeInFamilyDocument(doc, catalog, ct);
                if (!bakeResult.Success)
                    return bakeResult;

                SmartConLogger.Info(
                    $"BakeInExistingDocument: baked {bakeResult.BakedTypeCount} type(s) " +
                    $"into held-open document — snapshot will contain baked types");
                return bakeResult;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"BakeInExistingDocument failed: {ex.GetType().Name}: {ex.Message} " +
                    "[Action: verify the .rfa and .txt are compatible, or check the Revit journal for details]");
                return Failure(ex.Message);
            }
        }, ct).ConfigureAwait(false);
    }

    private FamilyTypeCatalogBakingResult BakeInFamilyDocument(
        Document familyDoc,
        TypeCatalogParseResult catalog,
        CancellationToken ct)
    {
        var fm = familyDoc.FamilyManager;
        var catalogParamNames = new HashSet<string>(catalog.ParameterNames, StringComparer.OrdinalIgnoreCase);
        var catalogColumnsByName = catalog.Columns
            .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var paramMap = BuildParameterMap(fm);
        var formulaState = CollectFormulaState(fm, paramMap, catalogParamNames);

        BakeStats stats = BakeStats.Empty;
        var txOk = _transactionService.RunInTransaction(familyDoc, BakeTransactionName, _ =>
        {
            EnsureCurrentType(fm);
            var anchor = fm.CurrentType!;
            fm.RenameCurrentType(AnchorTypeName);
            RemoveOtherTypes(fm, anchor);

            DisableFormulas(fm, formulaState);
            stats = CreateCatalogTypes(fm, catalog, paramMap, catalogColumnsByName, ct);
            RemoveAnchorType(fm);
            RestoreFormulas(fm, formulaState);

            familyDoc.Regenerate();
        });

        if (!txOk)
        {
            return Failure("Transaction failed while baking Type Catalog; see log for details");
        }

        // Один Info summary вместо per-param Debug — горячий цикл (до ~500 итераций на bake),
        // который раньше генерировал 120-500 строк [CatalogSet] / [UnitConvert] Debug на импорт.
        SmartConLogger.Info(
            $"Type Catalog baked: created {stats.CreatedCount} type(s), " +
            $"{stats.Converted} unit(s) converted, {stats.Failed} unit(s) failed, " +
            $"restored {formulaState.Count} formula(s)");

        return new FamilyTypeCatalogBakingResult(
            Success: true,
            OutputRfaPath: null,
            BakedTypeCount: catalog.Entries.Count,
            ErrorMessage: null);
    }

    private static void EnsureOutputDirectory(string outputRfaPath)
    {
        var directory = Path.GetDirectoryName(outputRfaPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void EnsureWritable(string outputRfaPath)
    {
        if (File.Exists(outputRfaPath))
        {
            var attributes = File.GetAttributes(outputRfaPath);
            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                File.SetAttributes(outputRfaPath, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static void SetReadOnly(string outputRfaPath)
    {
        if (File.Exists(outputRfaPath))
        {
            File.SetAttributes(outputRfaPath, File.GetAttributes(outputRfaPath) | FileAttributes.ReadOnly);
        }
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "<null>",
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ElementId id => id.ToString(),
            _ => value.ToString() ?? "<empty>"
        };
    }

    private static FamilyTypeCatalogBakingResult Failure(string message)
    {
        return new FamilyTypeCatalogBakingResult(false, null, 0, message);
    }

    private sealed record FormulaParameterState(
        FamilyParameter Parameter,
        string ParameterName,
        string Formula,
        IReadOnlyList<string> ReferencedVariables);

    /// <summary>
    /// Aggregated bake statistics returned by <see cref="CreateCatalogTypes"/> and
    /// forwarded to <see cref="BakeInFamilyDocument"/> for the final Info summary.
    /// Заменяет per-param Debug (C7 — hot loops &gt;100 iter запрещают Debug).
    /// </summary>
    private readonly record struct BakeStats(int CreatedCount, int Converted, int Failed)
    {
        public static BakeStats Empty => new(0, 0, 0);
    }

    private enum UnitConversionOutcome
    {
        /// <summary>Не Double или нет annotation — применять как есть.</summary>
        Skipped,
        /// <summary>Успешная конверсия mm/in/ft/etc. → Revit internal units.</summary>
        Converted,
        /// <summary>Annotation не распознана или несовместима со spec параметра — caller skip.</summary>
        Failed,
    }

    private readonly record struct UnitConversionResult(UnitConversionOutcome Outcome, object? FinalValue)
    {
        public static UnitConversionResult Skipped(object finalValue) => new(UnitConversionOutcome.Skipped, finalValue);
        public static UnitConversionResult Converted(double internalValue) => new(UnitConversionOutcome.Converted, internalValue);
        public static UnitConversionResult Failed() => new(UnitConversionOutcome.Failed, null);
    }
}

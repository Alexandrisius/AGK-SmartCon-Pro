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
public sealed class RevitFamilyTypeCatalogBaker : IFamilyTypeCatalogBaker
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
        using var _scope = SmartConLogger.BeginScope("TypeCatalogBake",
            ("Method", nameof(BakeAsync)),
            ("RfaFileName", Path.GetFileName(sourceRfaPath)),
            ("OutputFileName", Path.GetFileName(outputRfaPath)),
            ("CatalogRows", catalog.Entries.Count));

        return await _awaitableEvent.RaiseAsync<FamilyTypeCatalogBakingResult>(obj =>
        {
            var uiApp = (Autodesk.Revit.UI.UIApplication)obj;
            var app = uiApp.Application;
            Document? familyDoc = null;
            var openSw = System.Diagnostics.Stopwatch.StartNew();
            var saveSw = System.Diagnostics.Stopwatch.StartNew();

            SmartConLogger.FreezeThreadPool("Bake.beforeOpen");

            try
            {
                EnsureOutputDirectory(outputRfaPath);
                EnsureWritable(outputRfaPath);

                SmartConLogger.Freeze($"Bake: Starting OpenDocumentFile for '{Path.GetFileName(sourceRfaPath)}'");
                familyDoc = app.OpenDocumentFile(sourceRfaPath);
                openSw.Stop();
                SmartConLogger.Freeze($"Bake: OpenDocumentFile completed in {openSw.ElapsedMilliseconds}ms");
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

                saveSw.Restart();
                using var saveOptions = new SaveAsOptions { OverwriteExistingFile = true };
                familyDoc.SaveAs(outputRfaPath, saveOptions);
                SetReadOnly(outputRfaPath);
                saveSw.Stop();
                SmartConLogger.Freeze($"Bake: SaveAs completed in {saveSw.ElapsedMilliseconds}ms");
                SmartConLogger.Info($"Baked family saved to '{Path.GetFileName(outputRfaPath)}'");

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
                    $"Type Catalog bake failed for '{Path.GetFileName(sourceRfaPath)}': {ex.Message} " +
                    "[Action: verify the .rfa and .txt are compatible, or check the Revit journal for details]");
                return Failure(ex.Message);
            }
            finally
            {
                if (familyDoc != null)
                {
                    SmartConLogger.Freeze("Bake: Starting Close");
                    var closeSw = System.Diagnostics.Stopwatch.StartNew();
                    try { familyDoc.Close(false); }
                    catch (Exception ex)
                    {
                        closeSw.Stop();
                        SmartConLogger.FreezeFail("Bake.Close", $"after {closeSw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
                    }
                    closeSw.Stop();
                    SmartConLogger.Freeze($"Bake: Close completed in {closeSw.ElapsedMilliseconds}ms");

                    // See RevitFamilyDataExtractionService — RevitAPI Document is
                    // a managed RCW wrapper, not a real COM object. ReleaseComObject
                    // on it throws ArgumentException and leaves a half-cleaned-up
                    // RCW that the GC finalizer will mishandle, zombifying the WPF
                    // render thread (REVIT-237190). Skip the call when IsComObject
                    // returns false; Close(false) above is the real lifetime-end.
                    try { Marshal.ReleaseComObject(familyDoc); } catch { /* ignore */ }
                }

                // Freeze workaround (REVIT-236376 / REVIT-237190): family upgrade
                // dialog leaves the WPF render thread behind the UI thread. Show
                // a near-invisible InfoCenter balloon to force a Win32 focus event
                // that re-syncs them — the same recovery that happens when the
                // user right-clicks on the DockablePane. See REVIT API forum
                // "Loading a rfa file into a document using LoadFamily() freezes
                // Revit UI" for the community-confirmed workaround.
                RevitBalloonNudge.Nudge($"SmartCon: baked {Path.GetFileName(sourceRfaPath)}");
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

    private static Dictionary<string, FamilyParameter> BuildParameterMap(Autodesk.Revit.DB.FamilyManager fm)
    {
        var map = new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (FamilyParameter param in fm.Parameters)
        {
            if (param.Definition?.Name is string name)
            {
                map[name] = param;
            }
        }

        return map;
    }

    private List<FormulaParameterState> CollectFormulaState(
        Autodesk.Revit.DB.FamilyManager fm,
        Dictionary<string, FamilyParameter> paramMap,
        HashSet<string> catalogParamNames)
    {
        var states = new List<FormulaParameterState>();
        foreach (FamilyParameter param in fm.Parameters)
        {
            if (param.Definition?.Name is not string name) continue;
            if (string.IsNullOrWhiteSpace(param.Formula)) continue;
            if (!param.CanAssignFormula) continue;

            var variables = _formulaSolver.ExtractVariables(param.Formula);
            if (!variables.Any(v => catalogParamNames.Contains(v)))
            {
                continue;
            }

            states.Add(new FormulaParameterState(
                param,
                name,
                param.Formula,
                variables));
        }

        if (states.Count == 0)
        {
            SmartConLogger.Warn(
                $"Type Catalog bake: no formula-driven parameters reference catalog names " +
                $"[Action: verify that .txt column names match the variable names used in formulas]");
        }

        return states;
    }

    private static void EnsureCurrentType(Autodesk.Revit.DB.FamilyManager fm)
    {
        if (fm.CurrentType is not null)
        {
            return;
        }

        if (fm.Types.Size > 0)
        {
            foreach (FamilyType existing in fm.Types)
            {
                fm.CurrentType = existing;
                return;
            }
        }

        fm.CurrentType = fm.NewType(AnchorTypeName);
    }

    private static void RemoveOtherTypes(Autodesk.Revit.DB.FamilyManager fm, FamilyType anchor)
    {
        var typesToDelete = new List<FamilyType>();
        foreach (FamilyType familyType in fm.Types)
        {
            if (!string.Equals(familyType.Name, anchor.Name, StringComparison.Ordinal))
            {
                typesToDelete.Add(familyType);
            }
        }

        foreach (var type in typesToDelete)
        {
            try
            {
                fm.CurrentType = type;
                fm.DeleteCurrentType();
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Failed to delete existing type '{type.Name}': {ex.Message} " +
                    "[Action: continue baking, this type may be inherited from the template]");
            }
        }
    }

    private static void RemoveAnchorType(Autodesk.Revit.DB.FamilyManager fm)
    {
        var anchor = fm.Types.Cast<FamilyType>()
            .FirstOrDefault(t => string.Equals(t.Name, AnchorTypeName, StringComparison.Ordinal));

        if (anchor is null)
        {
            return;
        }

        if (fm.Types.Size == 1)
        {
            fm.CurrentType = anchor;
            fm.RenameCurrentType("DefaultType");
            return;
        }

        fm.CurrentType = anchor;
        fm.DeleteCurrentType();
    }

    private static void DisableFormulas(Autodesk.Revit.DB.FamilyManager fm, List<FormulaParameterState> states)
    {
        var currentType = fm.CurrentType ?? throw new InvalidOperationException("No current family type");

        foreach (var state in states)
        {
            try
            {
                var valueBefore = ReadParameterValue(currentType, state.Parameter);
                SmartConLogger.Debug(
                    $"[FormulaDisable] param='{state.ParameterName}' formula='{state.Formula}' " +
                    $"valueBefore={FormatValue(valueBefore)}");

                fm.SetFormula(state.Parameter, null);
                var valueAfterFormulaRemove = ReadParameterValue(currentType, state.Parameter);
                SmartConLogger.Debug(
                    $"[FormulaDisable] param='{state.ParameterName}' after SetFormula(null) " +
                    $"valueAfter={FormatValue(valueAfterFormulaRemove)}");

                if (valueBefore is not null)
                {
                    ApplyTypedValue(fm, state.Parameter, valueBefore);
                    var valueAfterSet = ReadParameterValue(currentType, state.Parameter);
                    SmartConLogger.Debug(
                        $"[FormulaDisable] param='{state.ParameterName}' after Set(valueBefore) " +
                        $"valueAfterSet={FormatValue(valueAfterSet)}");
                }

                SmartConLogger.Debug(
                    $"Disabled formula for '{state.ParameterName}' and preserved its current value");
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Failed to disable formula for '{state.ParameterName}': {ex.Message} " +
                    "[Action: bake may fail or produce an invalid family]");
                throw;
            }
        }
    }

    private BakeStats CreateCatalogTypes(
        Autodesk.Revit.DB.FamilyManager fm,
        TypeCatalogParseResult catalog,
        Dictionary<string, FamilyParameter> paramMap,
        Dictionary<string, TypeCatalogColumn> catalogColumnsByName,
        CancellationToken ct)
    {
        var createdCount = 0;
        var unitConverted = 0;
        var unitFailed = 0;

        foreach (var entry in catalog.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var newType = fm.NewType(entry.TypeName);
            if (newType is null)
            {
                SmartConLogger.Warn($"NewType returned null for '{entry.TypeName}' [Action: skipping type]");
                continue;
            }

            fm.CurrentType = newType;
            var entryStats = ApplyCatalogValues(fm, entry, paramMap, catalogColumnsByName);
            unitConverted += entryStats.Converted;
            unitFailed += entryStats.Failed;
            createdCount++;
        }

        return new BakeStats(createdCount, unitConverted, unitFailed);
    }

    private BakeStats ApplyCatalogValues(
        Autodesk.Revit.DB.FamilyManager fm,
        TypeCatalogEntry entry,
        Dictionary<string, FamilyParameter> paramMap,
        Dictionary<string, TypeCatalogColumn> catalogColumnsByName)
    {
        var unitConverted = 0;
        var unitFailed = 0;

        foreach (var kvp in entry.ParameterValues)
        {
            var columnName = kvp.Key;
            var rawValue = kvp.Value;

            if (!paramMap.TryGetValue(columnName, out var param) || param is null)
            {
                // Skip-debug: срабатывает только когда column .txt не имеет соответствующего
                // параметра в .rfa (не hot path — обычно 0 случаев на корректный каталог).
                SmartConLogger.Debug($"Skip(no-param): type='{entry.TypeName}' param='{columnName}'");
                continue;
            }

            if (param.IsReadOnly)
            {
                SmartConLogger.Debug($"Skip(readonly): type='{entry.TypeName}' param='{columnName}'");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(param.Formula))
            {
                SmartConLogger.Debug(
                    $"Skip(formula-driven): type='{entry.TypeName}' param='{columnName}'");
                continue;
            }

            var storageTypeCode = MapRevitStorageType(param.StorageType);
            var applyResult = _valueApplier.Apply(rawValue, storageTypeCode);
            if (applyResult.Status != TypeCatalogValueApplyStatus.Success)
            {
                SmartConLogger.Warn(
                    $"Value apply failed: type='{entry.TypeName}' param='{columnName}' " +
                    $"value='{rawValue}' status={applyResult.Status} err='{applyResult.Error}' " +
                    "[Action: skipping parameter, continuing with type]");
                continue;
            }

            try
            {
                var conversionResult = TryConvertUnit(
                    param, applyResult.Value, columnName, entry.TypeName, catalogColumnsByName);

                switch (conversionResult.Outcome)
                {
                    case UnitConversionOutcome.Skipped:
                        ApplyTypedValue(fm, param, conversionResult.FinalValue);
                        break;
                    case UnitConversionOutcome.Converted:
                        ApplyTypedValue(fm, param, conversionResult.FinalValue);
                        unitConverted++;
                        break;
                    case UnitConversionOutcome.Failed:
                        // Warn уже залогирован в TryConvertUnit.
                        unitFailed++;
                        break;
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Set failed: type='{entry.TypeName}' param='{columnName}' " +
                    $"ex={ex.GetType().Name}: {ex.Message} [Action: skipping parameter, continuing with type]");
            }
        }

        return new BakeStats(CreatedCount: 0, Converted: unitConverted, Failed: unitFailed);
    }

    /// <summary>
    /// Конвертирует Double значение в Revit internal units, если column header
    /// содержит <c>##UNIT##</c> annotation. Для Integer/String/ElementId — без изменений.
    /// Возвращает <see cref="UnitConversionResult"/> с одним из трёх исходов:
    /// <list type="bullet">
    ///   <item><see cref="UnitConversionOutcome.Skipped"/> — не Double или нет annotation; вернуть значение как есть.</item>
    ///   <item><see cref="UnitConversionOutcome.Converted"/> — успешная конверсия; вернуть конвертированное.</item>
    ///   <item><see cref="UnitConversionOutcome.Failed"/> — unit annotation не распознана; caller skip parameter.</item>
    /// </list>
    /// </summary>
    private static UnitConversionResult TryConvertUnit(
        FamilyParameter param,
        object? applyResultValue,
        string columnName,
        string entryTypeName,
        Dictionary<string, TypeCatalogColumn> catalogColumnsByName)
    {
        if (applyResultValue is null)
            return UnitConversionResult.Failed();

        // Unit conversion только для Double storage type с явной ##UNIT## annotation.
        if (param.StorageType != StorageType.Double)
            return UnitConversionResult.Skipped(applyResultValue);

        if (!catalogColumnsByName.TryGetValue(columnName, out var column) || column is null)
            return UnitConversionResult.Skipped(applyResultValue);

        if (!column.HasUnitAnnotation)
            return UnitConversionResult.Skipped(applyResultValue);

        var rawDouble = (double)applyResultValue;
        var internalValue = RevitUnitsCompat.CatalogCellToInternalUnits(
            rawDouble, column.UnitAnnotation, param);

        if (internalValue is null)
        {
            SmartConLogger.Warn(
                $"Unit annotation '{column.UnitAnnotation}' (##TYPE##={column.TypeAnnotation ?? "<none>"}) " +
                $"not recognized for type='{entryTypeName}' param='{columnName}' " +
                $"[Action: verify the .txt header matches Revit Type Catalog spec, " +
                $"or remove the ##TYPE##UNITS annotation to use project display units]");
            return UnitConversionResult.Failed();
        }

        return UnitConversionResult.Converted(internalValue.Value);
    }

    private static void RestoreFormulas(Autodesk.Revit.DB.FamilyManager fm, List<FormulaParameterState> states)
    {
        var ordered = OrderByDependency(states);

        foreach (var state in ordered)
        {
            try
            {
                var valueBefore = ReadParameterValue(fm.CurrentType!, state.Parameter);
                fm.SetFormula(state.Parameter, state.Formula);
                var valueAfter = ReadParameterValue(fm.CurrentType!, state.Parameter);
                SmartConLogger.Debug(
                    $"[FormulaRestore] param='{state.ParameterName}' formula='{state.Formula}' " +
                    $"before={FormatValue(valueBefore)} after={FormatValue(valueAfter)}");
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Failed to restore formula for '{state.ParameterName}': {ex.Message} " +
                    "[Action: the baked family may be invalid]");
                throw;
            }
        }
    }

    private static List<FormulaParameterState> OrderByDependency(List<FormulaParameterState> states)
    {
        var stateByName = states.ToDictionary(s => s.ParameterName, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FormulaParameterState>();

        foreach (var state in states)
        {
            Visit(state, stateByName, visited, result);
        }

        return result;
    }

    private static void Visit(
        FormulaParameterState state,
        Dictionary<string, FormulaParameterState> stateByName,
        HashSet<string> visited,
        List<FormulaParameterState> result)
    {
        if (!visited.Add(state.ParameterName))
        {
            return;
        }

        foreach (var variable in state.ReferencedVariables)
        {
            if (stateByName.TryGetValue(variable, out var dependency))
            {
                Visit(dependency, stateByName, visited, result);
            }
        }

        result.Add(state);
    }

    private static object? ReadParameterValue(FamilyType familyType, FamilyParameter param)
    {
        if (!familyType.HasValue(param))
        {
            return null;
        }

        return param.StorageType switch
        {
            StorageType.String => familyType.AsString(param),
            StorageType.Double => familyType.AsDouble(param),
            StorageType.Integer => familyType.AsInteger(param),
            StorageType.ElementId => familyType.AsElementId(param),
            _ => null
        };
    }

    private static void ApplyTypedValue(Autodesk.Revit.DB.FamilyManager fm, FamilyParameter param, object? value)
    {
        switch (param.StorageType)
        {
            case StorageType.String:
                fm.Set(param, (string)value!);
                break;
            case StorageType.Double:
                fm.Set(param, (double)value!);
                break;
            case StorageType.Integer:
                fm.Set(param, (int)value!);
                break;
            case StorageType.ElementId:
#if REVIT2022_OR_GREATER
                fm.Set(param, new ElementId((long)value!));
#else
                fm.Set(param, new ElementId((BuiltInParameter)(long)value!));
#endif
                break;
            default:
                throw new NotSupportedException($"StorageType '{param.StorageType}' is not supported");
        }
    }

    private static StorageTypeCode MapRevitStorageType(StorageType rt)
    {
        return (int)rt switch
        {
            0 => StorageTypeCode.StgNone,
            1 => StorageTypeCode.StgInt,
            2 => StorageTypeCode.StgNumber,
            3 => StorageTypeCode.StgText,
            4 => StorageTypeCode.StgElementId,
            _ => StorageTypeCode.StgNone,
        };
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

using System.IO;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Extracts types and parameter values from a Revit family (.rfa) document.
/// For families with a Type Catalog (.txt sidecar), simulates every type
/// listed in the catalog and reads back formula-driven values via
/// <c>Document.Regenerate()</c> (ADR-032, issue #66).
/// </summary>
public sealed class RevitFamilyDataExtractionService : IFamilyDataExtractionService
{
    private const string TempTypePrefix = "__SCAT__";
    private const string SimulationTransactionName = "SmartCon_TypeCatalogSimulation";
    private const string TxtExtension = ".txt";

    private readonly IRevitContext _revitContext;
    private readonly ITypeCatalogValueApplier _valueApplier;

    public RevitFamilyDataExtractionService(
        IRevitContext revitContext,
        ITypeCatalogValueApplier valueApplier)
    {
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
        _valueApplier = valueApplier ?? throw new ArgumentNullException(nameof(valueApplier));
    }

    public FamilyExtractionResult Extract(string rfaFilePath, IReadOnlyList<string> expectedParameterNames)
    {
        using var _scope = SmartConLogger.BeginScope("FamilyDataExt",
            ("Method", "Extract"),
            ("RfaFileName", Path.GetFileName(rfaFilePath)));
        var doc = _revitContext.GetDocument();
        var app = doc.Application;

        var revitMajorVersion = GetMajorVersion();

        Document? familyDoc = null;
        try
        {
            familyDoc = app.OpenDocumentFile(rfaFilePath);
            if (familyDoc is null)
            {
                return new FamilyExtractionResult(false, [], null, "Failed to open family document", revitMajorVersion);
            }

            if (!familyDoc.IsFamilyDocument)
            {
                return new FamilyExtractionResult(false, [], null, "Not a family document", revitMajorVersion);
            }

            return ExtractCore(familyDoc, expectedParameterNames, revitMajorVersion);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Extract failed for '{Path.GetFileName(rfaFilePath)}': {ex.Message} " +
                "[Action: verify file is a valid Revit .rfa, or check Revit version compatibility]");
            return new FamilyExtractionResult(false, [], null, ex.Message, revitMajorVersion);
        }
        finally
        {
            if (familyDoc != null)
            {
                try
                {
                    familyDoc.Close(false);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"Failed to close family document '{Path.GetFileName(rfaFilePath)}': {ex.Message} " +
                        "[Action: safe to ignore — Revit releases the document on its own]");
                }

                try
                {
                    Marshal.ReleaseComObject(familyDoc);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug(
                        $"Marshal.ReleaseComObject skipped (RevitAPI doc is not a real COM object): {ex.Message}");
                }
            }
        }
    }

    public FamilyExtractionResult Extract(Document familyDocument, IReadOnlyList<string> expectedParameterNames)
    {
#pragma warning disable CA1510
        if (familyDocument is null)
            throw new ArgumentNullException(nameof(familyDocument));
#pragma warning restore CA1510

        var revitMajorVersion = GetMajorVersion();

        if (!familyDocument.IsFamilyDocument)
        {
            return new FamilyExtractionResult(false, [], null, "Not a family document", revitMajorVersion);
        }

        try
        {
            return ExtractCore(familyDocument, expectedParameterNames, revitMajorVersion);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Extract failed for document '{familyDocument.Title}': {ex.Message} " +
                "[Action: check Revit journal for details, or restart Revit if the COM object is corrupted]");
            return new FamilyExtractionResult(false, [], null, ex.Message, revitMajorVersion);
        }
    }

    public FamilyExtractionResult ExtractFromManagedFile(
        string managedRfaPath,
        IReadOnlyList<string> expectedParameterNames,
        CancellationToken ct = default)
    {
#pragma warning disable CA1510
        if (string.IsNullOrEmpty(managedRfaPath))
            throw new ArgumentNullException(nameof(managedRfaPath));
#pragma warning restore CA1510

        using var _scope = SmartConLogger.BeginScope("FamilyDataExt",
            ("Method", "ExtractFromManagedFile"),
            ("RfaFileName", Path.GetFileName(managedRfaPath)));

        if (!File.Exists(managedRfaPath))
        {
            return new FamilyExtractionResult(
                false, [], null,
                $"File not found: {Path.GetFileName(managedRfaPath)}",
                GetMajorVersion());
        }

        var txtPath = Path.ChangeExtension(managedRfaPath, TxtExtension);
        var hasTypeCatalog = File.Exists(txtPath);

        Document? doc = null;
        try
        {
            doc = _revitContext.GetDocument().Application.OpenDocumentFile(managedRfaPath);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"OpenDocumentFile failed for '{Path.GetFileName(managedRfaPath)}': {ex.Message} " +
                "[Action: verify file is a valid Revit .rfa, or check Revit version compatibility]");
            return new FamilyExtractionResult(
                false, [], null, ex.Message, GetMajorVersion());
        }

        if (doc is null)
        {
            return new FamilyExtractionResult(
                false, [], null, "OpenDocumentFile returned null", GetMajorVersion());
        }

        try
        {
            if (!doc.IsFamilyDocument)
            {
                return new FamilyExtractionResult(
                    false, [], null, "Not a family document", GetMajorVersion());
            }

            return hasTypeCatalog
                ? ExtractWithTypeCatalog(doc, txtPath, expectedParameterNames, ct)
                : ExtractCore(doc, expectedParameterNames, GetMajorVersion());
        }
        finally
        {
            try { doc.Close(false); } catch { /* ignore close failure */ }
            try { Marshal.ReleaseComObject(doc); } catch { /* RevitAPI doc is not real COM */ }
        }
    }

    private FamilyExtractionResult ExtractWithTypeCatalog(
        Document familyDoc,
        string txtPath,
        IReadOnlyList<string> expectedParameterNames,
        CancellationToken ct)
    {
        var txtFileName = Path.GetFileName(txtPath);
        var revitMajorVersion = GetMajorVersion();

        if (!File.Exists(txtPath))
        {
            SmartConLogger.Debug(
                $"Type catalog file disappeared mid-flight: '{txtFileName}' — falling back to plain Extract");
            return ExtractCore(familyDoc, expectedParameterNames, revitMajorVersion);
        }

        var parseResult = ParseTypeCatalog(txtPath, txtFileName);
        if (parseResult is null)
        {
            return ExtractCore(familyDoc, expectedParameterNames, revitMajorVersion);
        }

        var simulationStart = DateTimeOffset.UtcNow;
        using var _simScope = SmartConLogger.BeginScope("TypeCatalogSim",
            ("Method", nameof(ExtractWithTypeCatalog)),
            ("TxtFileName", txtFileName),
            ("Rows", parseResult.Entries.Count));
        SmartConLogger.Info(
            $"START rows={parseResult.Entries.Count}");

        var fm = familyDoc.FamilyManager;
        var paramMap = new Dictionary<string, FamilyParameter>(StringComparer.Ordinal);
        foreach (FamilyParameter param in fm.Parameters)
        {
            if (param.Definition?.Name is string name)
            {
                paramMap[name] = param;
            }
        }

        var results = new List<FamilyExtractionTypeValues>();
        var skipped = 0;
        var errors = 0;

        using (Transaction tx = new Transaction(familyDoc, SimulationTransactionName))
        {
            // Install IFailuresPreprocessor to suppress modal error dialogs from formula-driven
            // parameters (e.g. "ADSK_Коэффициент мощности must be 0..1" as an INTERMEDIATE
            // state during Set — formulas compute correctly once all values are set).
            // See https://jeremytammik.github.io/tbc/a/1951_disable_error_failure.html
            // for the canonical pattern (SetForcedModalHandling + ProceedWithRollBack).
            var failureOptions = tx.GetFailureHandlingOptions();
            failureOptions.SetFailuresPreprocessor(new SilentFailurePreprocessor());
            tx.SetFailureHandlingOptions(failureOptions);

            try
            {
                tx.Start();

                for (var rowIndex = 0; rowIndex < parseResult.Entries.Count; rowIndex++)
                {
                    if (ct.IsCancellationRequested) break;
                    var entry = parseResult.Entries[rowIndex];
                    var tempTypeName = TempTypePrefix + entry.TypeName;
                    var typeStart = DateTimeOffset.UtcNow;
                    var setOk = 0;
                    var setSkip = 0;

                    try
                    {
                        FamilyType? tempType;
                        try
                        {
                            tempType = fm.NewType(tempTypeName);
                        }
                        catch (Exception newTypeEx)
                        {
                            SmartConLogger.Warn(
                                $"NewType failed: row={rowIndex} name='{entry.TypeName}' " +
                                $"ex={newTypeEx.GetType().Name}: {newTypeEx.Message} " +
                                "[Action: пропускаем типоразмер, продолжаем]");
                            errors++;
                            continue;
                        }

                        if (tempType is null)
                        {
                            SmartConLogger.Warn(
                                $"NewType returned null: row={rowIndex} name='{entry.TypeName}' " +
                                "[Action: пропускаем типоразмер, продолжаем]");
                            skipped++;
                            continue;
                        }

                        // CRITICAL: FamilyManager.Set writes to the CURRENT family type
                        // (Tammik, Autodesk Forums). NewType already sets the new type as
                        // current, but we set it explicitly to be safe.
                        try { fm.CurrentType = tempType; }
                        catch (Exception currentTypeEx)
                        {
                            SmartConLogger.Warn(
                                $"CurrentType switch failed: row={rowIndex} name='{entry.TypeName}' " +
                                $"ex={currentTypeEx.GetType().Name}: {currentTypeEx.Message}");
                        }

                        SmartConLogger.Debug(
                            $"[DIAG] TypeCatalog row={rowIndex} name='{entry.TypeName}': " +
                            $"paramValues={entry.ParameterValues.Count} " +
                            $"firstKeys={SampleKeys(entry.ParameterValues, 3)} " +
                            $"firstValues={SampleValues(entry.ParameterValues, 3)}");

                        foreach (var kvp in entry.ParameterValues)
                        {
                            var columnName = kvp.Key;
                            var rawValue = kvp.Value;

                            if (!paramMap.TryGetValue(columnName, out var param) || param is null)
                            {
                                SmartConLogger.Debug(
                                    $"[DIAG] Skip(no-param): row={rowIndex} column='{columnName}'");
                                setSkip++;
                                continue;
                            }
                            if (param.IsReadOnly)
                            {
                                SmartConLogger.Debug(
                                    $"[DIAG] Skip(readonly): row={rowIndex} column='{columnName}'");
                                setSkip++;
                                continue;
                            }

                            var storageTypeCode = MapRevitStorageType(param.StorageType);
                            var applyResult = _valueApplier.Apply(rawValue, storageTypeCode);
                            if (applyResult.Status != TypeCatalogValueApplyStatus.Success)
                            {
                                SmartConLogger.Warn(
                                    $"Value apply failed: row={rowIndex} " +
                                    $"param='{columnName}' value='{rawValue}' " +
                                    $"stg={param.StorageType}({(int)param.StorageType})→{storageTypeCode} " +
                                    $"status={applyResult.Status} err='{applyResult.Error}' " +
                                    "[Action: пропускаем параметр, продолжаем]");
                                setSkip++;
                                continue;
                            }

                            SmartConLogger.Debug(
                                $"[DIAG] Apply ok: row={rowIndex} param='{columnName}' " +
                                $"stg={param.StorageType} valueType='{applyResult.Value?.GetType().Name}' " +
                                $"value='{rawValue}'");

                            try
                            {
                                ApplyTypedValue(fm, param, applyResult.Value);
                                setOk++;
                            }
                            catch (Exception setEx)
                            {
                                SmartConLogger.Warn(
                                    $"Set failed: row={rowIndex} name='{entry.TypeName}' " +
                                    $"param='{columnName}' " +
                                    $"valueType='{applyResult.Value?.GetType().Name}' " +
                                    $"stg={param.StorageType} " +
                                    $"ex={setEx.GetType().Name}: {setEx.Message} " +
                                    "[Action: пропускаем параметр, продолжаем]");
                                setSkip++;
                            }
                        }

                        try
                        {
                            familyDoc.Regenerate();
                        }
                        catch (Exception regEx)
                        {
                            SmartConLogger.Warn(
                                $"Regenerate failed: row={rowIndex} name='{entry.TypeName}' " +
                                $"ex={regEx.GetType().Name}: {regEx.Message} " +
                                "[Action: типоразмер пропущен, продолжаем со следующим]");
                            errors++;
                            continue;
                        }

                        var values = new List<FamilyExtractionValueResult>();
                        foreach (FamilyParameter param in fm.Parameters)
                        {
                            if (param.Definition?.Name is not string paramName) continue;
                            if (!tempType.HasValue(param)) continue;
                            values.Add(ExtractValueForParameter(tempType, paramName, paramMap));
                        }

                        results.Add(new FamilyExtractionTypeValues(
                            entry.TypeName, rowIndex, values));

                        var typeElapsedMs = (long)(DateTimeOffset.UtcNow - typeStart).TotalMilliseconds;
                        SmartConLogger.Debug(
                            $"PerType: row={rowIndex} name='{entry.TypeName}' " +
                            $"set_ok={setOk} set_skip={setSkip} read={values.Count} " +
                            $"elapsed={typeElapsedMs}ms");
                    }
                    catch (Exception typeEx)
                    {
                        SmartConLogger.Warn(
                            $"Type failed: row={rowIndex} name='{entry.TypeName}' " +
                            $"ex={typeEx.GetType().Name}: {typeEx.Message} " +
                            "[Action: типоразмер пропущен, продолжаем]");
                        errors++;
                    }
                }

                tx.RollBack();
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started)
                {
                    try { tx.RollBack(); } catch { /* ignore */ }
                }
                throw;
            }
        }

        var sorted = results
            .OrderBy(t => t.TypeName, StringComparer.OrdinalIgnoreCase)
            .Select((t, i) => new FamilyExtractionTypeValues(t.TypeName, i, t.Values))
            .ToList();

        var totalElapsed = (long)(DateTimeOffset.UtcNow - simulationStart).TotalMilliseconds;
        SmartConLogger.Info(
            $"END simulated={sorted.Count}/{parseResult.Entries.Count} " +
            $"skipped={skipped} errors={errors} elapsed={totalElapsed}ms");

        var errorMessage = errors > 0
            ? $"{errors} type(s) skipped due to formula or set errors; see log for details"
            : null;

        return new FamilyExtractionResult(
            Success: true,
            Types: sorted,
            UntypedValues: null,
            ErrorMessage: errorMessage,
            RevitMajorVersion: revitMajorVersion);
    }

    /// <summary>
    /// IFailuresPreprocessor that suppresses modal error dialogs ONLY during the type-catalog
    /// simulation transaction. The pattern follows the official Autodesk guidance for
    /// <c>https://jeremytammik.github.io/tbc/a/1951_disable_error_failure.html</c>:
    /// <list type="bullet">
    /// <item><c>SetForcedModalHandling(false)</c> disables modal error dialogs (the critical step
    /// my previous attempt missed — calling <c>ProceedWithRollBack</c> without this flag
    /// causes a race condition between Revit trying to display the dialog and rollback the
    /// transaction, which crashes Revit).</item>
    /// <item><c>SetClearAfterRollback(true)</c> silences residual failure messages after rollback.</item>
    /// <item><c>ProceedWithRollBack</c> tells Revit to auto-rollback the failing transaction
    /// (we always do this ourselves in finally, but the duplicate is safe — no-op on already
    /// rolled-back transaction).</item>
    /// </list>
    /// We only suppress failures for OUR simulation transaction (named
    /// <see cref="SimulationTransactionName"/>); all other transactions get the default
    /// <c>Continue</c> behavior so we never silently swallow failures outside our scope.
    /// </summary>
    private sealed class SilentFailurePreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var transactionName = failuresAccessor.GetTransactionName();
            if (transactionName != SimulationTransactionName)
            {
                return FailureProcessingResult.Continue;
            }

            var messages = failuresAccessor.GetFailureMessages();
            var count = messages?.Count ?? 0;
            if (count > 0)
            {
                var bySeverity = messages!
                    .GroupBy(m => m.GetSeverity())
                    .Select(g => $"{g.Key}={g.Count()}")
                    .ToList();
                SmartConLogger.Debug(
                    $"[RevitFailure] simulation suppressed {count} failure(s): [" +
                    string.Join(", ", bySeverity) +
                    "] [Action: SetForcedModalHandling(false)+ProceedWithRollBack — модальный dialog подавлен, формулы в итоге вычисляются корректно]");
            }

            try
            {
                var options = failuresAccessor.GetFailureHandlingOptions();
                options.SetClearAfterRollback(true);
                options.SetForcedModalHandling(false);
                failuresAccessor.SetFailureHandlingOptions(options);
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug(
                    $"[RevitFailure] could not adjust FailureHandlingOptions: {ex.GetType().Name}: {ex.Message}");
            }

            return FailureProcessingResult.ProceedWithRollBack;
        }
    }

    /// <summary>
    /// Reads the .txt file with encoding detection (UTF-8 → UTF.Unknown → ANSI fallback).
    /// Pure helper kept here because Core has no UTF.Unknown dependency.
    /// </summary>
    private static TypeCatalogParseResult? ParseTypeCatalog(string txtPath, string txtFileName)
    {
        string content;
        try
        {
            var bytes = File.ReadAllBytes(txtPath);
            content = DetectAndDecodeText(bytes);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Type catalog read failed: '{txtFileName}' " +
                $"ex={ex.GetType().Name}: {ex.Message} " +
                "[Action: импорт продолжен без симуляции, параметры покажут дефолтные значения]");
            return null;
        }

        var parseResult = TypeCatalogParser.Parse(content);

        if (!parseResult.HasEntries)
        {
            SmartConLogger.Warn(
                $"Type catalog parse produced no rows: '{txtFileName}' " +
                "[Action: импорт продолжен без симуляции, параметры покажут дефолтные значения]");
            return null;
        }

        return parseResult;
    }

    /// <summary>
    /// Decodes text file bytes with BOM-aware encoding detection:
    /// UTF-16 LE/BE BOM → UTF-8 BOM → UTF-8 strict → UTF.Unknown detector → system ANSI.
    /// Mirrors <see cref="LocalFamilyImportService.ReadTypeCatalogWithEncodingFallback"/>
    /// (ADR-023-003). Detects \0 bytes as a UTF-16 fallback marker.
    /// </summary>
    private static string DetectAndDecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        var utf8String = System.Text.Encoding.UTF8.GetString(bytes);
        if (utf8String.Contains('\0'))
            return System.Text.Encoding.Unicode.GetString(bytes);

        if (utf8String.Contains('\uFFFD'))
        {
            try
            {
                var result = UtfUnknown.CharsetDetector.DetectFromBytes(bytes);
                if (result.Detected?.Confidence > 0.7f && result.Detected.Encoding != null)
                {
                    return result.Detected.Encoding.GetString(bytes);
                }

                var ansiCodePage = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
                return System.Text.Encoding.GetEncoding(ansiCodePage).GetString(bytes);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"Charset detection failed: {ex.Message}, returning UTF-8 result with replacement chars");
            }
        }

        return utf8String;
    }

    private static FamilyExtractionResult ExtractCore(Document familyDoc, IReadOnlyList<string> expectedParameterNames, int revitMajorVersion)
    {
        var fm = familyDoc.FamilyManager;

        var paramMap = new Dictionary<string, FamilyParameter>(StringComparer.Ordinal);
        foreach (FamilyParameter param in fm.Parameters)
        {
            if (param.Definition?.Name is string name)
            {
                paramMap[name] = param;
            }
        }

        var parametersToExtract = expectedParameterNames.Count > 0
            ? expectedParameterNames
            : paramMap.Keys.ToList();

        var allTypes = new List<FamilyExtractionTypeValues>();
        List<FamilyExtractionValueResult>? untypedValues = null;

        if (fm.Types.Size > 0)
        {
            foreach (FamilyType familyType in fm.Types)
            {
                var values = new List<FamilyExtractionValueResult>();

                foreach (var paramName in parametersToExtract)
                {
                    var value = ExtractValueForParameter(familyType, paramName, paramMap);
                    values.Add(value);
                }

                if (string.IsNullOrWhiteSpace(familyType.Name))
                {
                    untypedValues = values;
                }
                else
                {
                    allTypes.Add(new FamilyExtractionTypeValues(
                        familyType.Name, 0, values));
                }
            }
        }
        else
        {
            SmartConLogger.Info($"fm.Types.Size=0 — creating temporary type to read parameter values");

            using (Transaction tx = new Transaction(familyDoc, "SmartCon_TempTypeExtraction"))
            {
                tx.Start();
                FamilyType? tempType = null;
                try
                {
                    tempType = fm.NewType("_SmartConTemp");
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"Failed to create temporary type: {ex.Message} " +
                        "[Action: extraction will return empty values for this family]");
                }

                if (tempType is not null)
                {
                    var values = new List<FamilyExtractionValueResult>();
                    foreach (var paramName in parametersToExtract)
                    {
                        var value = ExtractValueForParameter(tempType, paramName, paramMap);
                        values.Add(value);
                    }

                    untypedValues = values;
                }

                tx.RollBack();
            }
        }

        var types = allTypes
            .OrderBy(t => t.TypeName, StringComparer.OrdinalIgnoreCase)
            .Select((t, i) => new FamilyExtractionTypeValues(t.TypeName, i, t.Values))
            .ToList();

        SmartConLogger.Info($"RESULT: {types.Count} named types, UntypedValues={(untypedValues is not null ? untypedValues.Count.ToString() : "null")}");

        return new FamilyExtractionResult(true, types, untypedValues, null, revitMajorVersion);
    }

    private static FamilyExtractionValueResult ExtractValueForParameter(
        FamilyType familyType, string parameterName,
        Dictionary<string, FamilyParameter> paramMap)
    {
        if (!paramMap.TryGetValue(parameterName, out var param))
        {
            return new FamilyExtractionValueResult(
                parameterName, null, null, null, null, null, null,
                AttributeValueStatus.MissingParameter,
                $"Parameter '{parameterName}' not found in family");
        }

        if (!familyType.HasValue(param))
        {
            return new FamilyExtractionValueResult(
                parameterName,
                param.IsInstance ? AttributeScope.Instance : AttributeScope.Type,
                param.StorageType.ToString(),
                null, null, null, null,
                AttributeValueStatus.EmptyValue,
                "Parameter has no value");
        }

        try
        {
            string? valueText = null;
            string? valueRaw = null;
            double? valueNumber = null;
            string? unitTypeId = null;

            switch (param.StorageType)
            {
                case StorageType.String:
                    var strVal = familyType.AsString(param);
                    valueText = strVal;
                    valueRaw = strVal;
                    break;
                case StorageType.Double:
                    var dblVal = familyType.AsDouble(param);
                    valueNumber = dblVal;
                    valueRaw = FormattableString.Invariant($"{dblVal}");
                    valueText = familyType.AsValueString(param);
                    break;
                case StorageType.Integer:
                    var intVal = familyType.AsInteger(param);
                    valueNumber = intVal;
                    valueRaw = intVal.ToString();
                    valueText = intVal.ToString();
                    break;
                case StorageType.ElementId:
                    var elemId = familyType.AsElementId(param);
                    valueText = elemId?.ToString();
                    valueRaw = elemId?.ToString();
                    break;
                default:
                    return new FamilyExtractionValueResult(
                        parameterName,
                        param.IsInstance ? AttributeScope.Instance : AttributeScope.Type,
                        param.StorageType.ToString(),
                        null, null, null, null,
                        AttributeValueStatus.UnsupportedStorageType,
                        $"StorageType '{param.StorageType}' is not supported");
            }

#if REVIT2021_OR_GREATER
            try { unitTypeId = param.GetUnitTypeId()?.TypeId; } catch { }
#endif

            return new FamilyExtractionValueResult(
                parameterName,
                param.IsInstance ? AttributeScope.Instance : AttributeScope.Type,
                param.StorageType.ToString(),
                valueText, valueRaw, valueNumber, unitTypeId,
                AttributeValueStatus.Found, null);
        }
        catch (Exception ex)
        {
            return new FamilyExtractionValueResult(
                parameterName,
                param.IsInstance ? AttributeScope.Instance : AttributeScope.Type,
                param.StorageType.ToString(),
                null, null, null, null,
                AttributeValueStatus.ReadError, ex.Message);
        }
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
                // ElementId(long) constructor added in Revit 2022.
                // For R21 use the BuiltInParameter overload (raw id cast).
#if REVIT2022_OR_GREATER
                fm.Set(param, new ElementId((long)value!));
#else
                fm.Set(param, new ElementId((BuiltInParameter)(long)value!));
#endif
                break;
            default:
                throw new NotSupportedException(
                    $"StorageType '{param.StorageType}' is not supported by ApplyTypedValue");
        }
    }

    private int GetMajorVersion()
    {
        var versionString = _revitContext.GetRevitVersion();
        return int.TryParse(versionString, out var v) ? v : 0;
    }

    /// <summary>
    /// Project-wide helper for converting an ElementId to a string. Uses the modern
    /// <c>Value</c> property in Revit 2024+ and falls back to <c>IntegerValue</c>
    /// (with a CS0618 pragma) on Revit 2019-2023. Mirrors the pattern in
    /// <c>RevitFamilyFinder.cs</c> / <c>RevitFittingInsertService.cs</c>.
    /// </summary>
    private static string ElementIdToInvariantString(Autodesk.Revit.DB.ElementId id)
    {
#if REVIT2024_OR_GREATER
        return id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
#else
#pragma warning disable CS0618
        return id.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
#pragma warning restore CS0618
#endif
    }

    /// <summary>
    /// Maps RevitAPI <c>StorageType</c> to our <see cref="StorageTypeCode"/>.
    /// The underlying integer values of <c>Autodesk.Revit.DB.StorageType</c> have been
    /// stable across ALL Revit versions (2019 through 2026+):
    /// <c>None=0, Integer=1, Double=2, String=3, ElementId=4</c>. Confirmed via
    /// <see href="https://www.revitapidocs.com/2025/3dbebcb8-792b-a3dd-fe63-faaa05704f3c.htm"/>
    /// and exa search across multiple versions. Therefore this mapping is identical
    /// for every target framework (R19/R21/R24/R25).
    /// </summary>
    private static StorageTypeCode MapRevitStorageType(Autodesk.Revit.DB.StorageType rt)
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

    private static string SampleKeys(IReadOnlyDictionary<string, string> dict, int max)
    {
        if (dict.Count == 0) return "<empty>";
        var keys = dict.Keys.Take(max).ToList();
        return "[" + string.Join("|", keys.Select(k => Truncate(k, 40))) + "]";
    }

    private static string SampleValues(IReadOnlyDictionary<string, string> dict, int max)
    {
        if (dict.Count == 0) return "<empty>";
        var values = dict.Take(max).Select(kv => Truncate(kv.Value, 40)).ToList();
        return "[" + string.Join("|", values) + "]";
    }

    private static string Truncate(string s, int max)
    {
#pragma warning disable CA1845
        return s.Length <= max ? s : s.Substring(0, max) + "…";
#pragma warning restore CA1845
    }
}

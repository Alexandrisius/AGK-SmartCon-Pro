using System.IO;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Extracts types and parameter values from a Revit family (.rfa) document.
/// Assumes Type Catalog types are already baked into the managed .rfa
/// (ADR-033); no simulation is performed here.
/// </summary>
public sealed class RevitFamilyDataExtractionService : IFamilyDataExtractionService
{
    private readonly IRevitContext _revitContext;

    public RevitFamilyDataExtractionService(
        IRevitContext revitContext)
    {
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
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

        Document? doc = null;
        SmartConLogger.FreezeThreadPool("ExtractFromManagedFile.beforeOpen");
        var openSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            SmartConLogger.Freeze($"Extract: Starting OpenDocumentFile for '{Path.GetFileName(managedRfaPath)}'");
            doc = _revitContext.GetDocument().Application.OpenDocumentFile(managedRfaPath);
            openSw.Stop();
            SmartConLogger.Freeze($"Extract: OpenDocumentFile completed in {openSw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            openSw.Stop();
            SmartConLogger.FreezeFail("Extract.OpenDocumentFile", $"after {openSw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
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

            return ExtractCore(doc, expectedParameterNames, GetMajorVersion());
        }
        finally
        {
            SmartConLogger.Freeze("Extract: Starting Close");
            var closeSw = System.Diagnostics.Stopwatch.StartNew();
            try { doc.Close(false); }
            catch (Exception ex)
            {
                closeSw.Stop();
                SmartConLogger.FreezeFail("Extract.Close", $"after {closeSw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
            }
            closeSw.Stop();
            SmartConLogger.Freeze($"Extract: Close completed in {closeSw.ElapsedMilliseconds}ms");

            // COM cleanup: RevitAPI Document is a managed RCW wrapper, not a real
            // unmanaged COM object. Calling Marshal.ReleaseComObject on a managed
            // wrapper throws ArgumentException ("Object must be of type __ComObject")
            // and — more importantly — leaves a half-cleaned-up RCW that the GC
            // finalizer will try to release later. On Windows 11 / Revit 2023 <
            // 2023.1.8 / Revit 2025 < 2025.4.3, that finalizer pass deadlocks the
            // finalizer thread, which in turn zombifies the WPF render thread —
            // the DockablePane "freezes" until the user right-clicks (which forces
            // a hit-test that wakes the render thread). See Autodesk REVIT-237190 /
            // REVIT-236376 in the revit-api-best-practice skill, file
            // references/async-threading-patterns.md, section "The #3 Fatal Bug:
            // Family Upgrade Freeze (COM/Finalizer Deadlock)".
            //
            // Marshal.IsComObject checks the actual underlying type: returns true
            // for a real RCW pointing at an unmanaged COM object, false for a pure
            // managed wrapper. RevitAPI Document is the latter, so we skip
            // ReleaseComObject and let GC reclaim it. Close(false) above is the
            // actual lifetime-end call — that is the one that tells Revit to drop
            // the document.
            SmartConLogger.Freeze("Extract: Starting ReleaseComObject");
            try { Marshal.ReleaseComObject(doc); }
            catch (Exception ex)
            {
                SmartConLogger.Freeze($"Extract: Close/Release failed - {ex.GetType().Name}: {ex.Message}");
            }
        }
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

    private int GetMajorVersion()
    {
        var versionString = _revitContext.GetRevitVersion();
        return int.TryParse(versionString, out var v) ? v : 0;
    }
}

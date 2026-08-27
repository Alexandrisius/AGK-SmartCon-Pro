using System.IO;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Util;

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

    public FamilyExtractionResult ExtractFromManagedFile(
        string managedRfaPath,
        IReadOnlyList<string> expectedParameterNames,
        CancellationToken ct = default)
    {
#pragma warning disable CA1510
        if (string.IsNullOrEmpty(managedRfaPath))
            throw new ArgumentNullException(nameof(managedRfaPath));
#pragma warning restore CA1510

        var rfaFileName = Path.GetFileName(managedRfaPath);

        if (!File.Exists(managedRfaPath))
        {
            return new FamilyExtractionResult(
                false, [], null,
                $"File not found: {rfaFileName}",
                GetMajorVersion(),
                Array.Empty<string>());
        }

        Document? doc = null;
        SmartConLogger.FreezeThreadPool("ExtractFromManagedFile.beforeOpen");
        double openMs;
        using (var _openMs = SmartConLogger.Measure("ExtractFromManagedFile.OpenDocumentFile"))
        {
            try
            {
                SmartConLogger.Freeze($"Extract: Starting OpenDocumentFile for '{rfaFileName}'");
                doc = _revitContext.GetDocument().Application.OpenDocumentFile(managedRfaPath);
                openMs = _openMs.GetElapsedMilliseconds();
                SmartConLogger.Freeze($"Extract: OpenDocumentFile completed in {openMs}ms");
            }
            catch (Exception ex)
            {
                openMs = _openMs.GetElapsedMilliseconds();
                SmartConLogger.FreezeFail("Extract.OpenDocumentFile", $"after {openMs}ms: {ex.GetType().Name}: {ex.Message}");
                SmartConLogger.Warn(
                    $"OpenDocumentFile failed for '{rfaFileName}': {ex.Message} " +
                    "[Action: verify file is a valid Revit .rfa, or check Revit version compatibility]");
                return new FamilyExtractionResult(
                    false, [], null, ex.Message, GetMajorVersion(), Array.Empty<string>());
            }
        }

        if (doc is null)
        {
            return new FamilyExtractionResult(
                false, [], null, "OpenDocumentFile returned null", GetMajorVersion(), Array.Empty<string>());
        }

        ParameterUnitDiagnostics.LogDocumentUnits(doc, "ManagedFile");

        try
        {
            if (!doc.IsFamilyDocument)
            {
                return new FamilyExtractionResult(
                    false, [], null, "Not a family document", GetMajorVersion(), Array.Empty<string>());
            }

            var result = ExtractCore(doc, expectedParameterNames, GetMajorVersion());

            // ADR-034: collect names of shared nested families declared by the
            // parent family, in the same OpenDocumentFile+Close cycle. This
            // closes REVIT-198137 for Revit 2023 / 2024 < 24.3.0.13 by
            // persisting the names at import time so the load path can use
            // them as a fallback when the Revit API returns null for the
            // shared family reference in IFamilyLoadOptions.OnSharedFamilyFound.
            //
            // We do NOT use a separate extractor / second OpenDocumentFile
            // (which was tried in the V2 implementation and caused a 4-dialog
            // MFC upgrade prompt per 2 .rfa files instead of 2). Sharing the
            // same open cycle keeps the freeze workaround (REVIT-236376 /
            // REVIT-237190) working: the WPF render thread is resynced once
            // per file, not twice, and the family-upgrade dialog appears
            // exactly once per .rfa.
            try
            {
                ct.ThrowIfCancellationRequested();
                var sharedNames = CollectSharedNestedFamilyNames(doc, ct);
                if (sharedNames.Count > 0)
                {
                    result = result with { SharedNestedFamilyNames = sharedNames };
                }
                else
                {
                    result = result with { SharedNestedFamilyNames = Array.Empty<string>() };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Shared nested name collection failed for '{Path.GetFileName(managedRfaPath)}': " +
                    $"{ex.GetType().Name}: {ex.Message} " +
                    "[Action: import continues without shared-nested names — Revit ≤ 2024.2 dialog will show placeholder, user can re-import to fix]");
            }

            return result;
        }
        finally
        {
            SmartConLogger.Freeze("Extract: Starting Close");
            using (var _closeMs = SmartConLogger.Measure("ExtractFromManagedFile.Close"))
            {
                try { doc.Close(false); }
                catch (Exception ex)
                {
                    var closeMs = _closeMs.GetElapsedMilliseconds();
                    SmartConLogger.FreezeFail("Extract.Close", $"after {closeMs}ms: {ex.GetType().Name}: {ex.Message}");
                }
                var closeMsFinal = _closeMs.GetElapsedMilliseconds();
                SmartConLogger.Freeze($"Extract: Close completed in {closeMsFinal}ms");
            }

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
            //
            // Log level is Debug (not Freeze) because the ArgumentException is
            // expected and harmless. The Freeze level is reserved for diagnostic
            // markers around long-running operations (OpenDocumentFile, Close, etc.).
            try { Marshal.ReleaseComObject(doc); }
            catch (Exception ex)
            {
                SmartConLogger.Debug(
                    $"Marshal.ReleaseComObject skipped (RevitAPI doc is not a real COM object): {ex.Message}");
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
                    var value = ExtractValueForParameter(familyType, paramName, paramMap, familyDoc);
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
                        var value = ExtractValueForParameter(tempType, paramName, paramMap, familyDoc);
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

        return new FamilyExtractionResult(true, types, untypedValues, null, revitMajorVersion, Array.Empty<string>());
    }

    private static FamilyExtractionValueResult ExtractValueForParameter(
        FamilyType familyType, string parameterName,
        Dictionary<string, FamilyParameter> paramMap,
        Document familyDoc)
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
                    valueText = dblVal.HasValue
                        ? Compatibility.RevitUnitsCompat.FormatDisplayValue(familyDoc, param, dblVal.Value)
                            ?? Core.Services.Implementation.UnitSymbolFixup.Correct(familyType.AsValueString(param))
                        : Core.Services.Implementation.UnitSymbolFixup.Correct(familyType.AsValueString(param));
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

    /// <summary>
    /// Walks the <c>FamilyInstance</c>s in the open family document and returns
    /// the names of nested families that are marked as shared. The result is
    /// deduplicated case-insensitively and returned in first-encountered order.
    ///
    /// ADR-034: this used to be its own class (<c>RevitSharedNestedFamilyExtractor</c>)
    /// that opened the .rfa a second time, which caused the Revit MFC family-upgrade
    /// dialog to appear 4 times per 2 imported files instead of 2 (one
    /// <c>OpenDocumentFile</c> per <c>ExtractFromManagedFile</c> call — and another
    /// per <c>ExtractCore</c>-shaped pass). Co-locating the scan inside the
    /// existing open-close cycle keeps the user-visible dialog count at the
    /// intended 1-per-file baseline.
    ///
    /// Detection rule: <c>Family</c> has no <c>IsShared</c> property in the Revit
    /// API — the canonical test is the built-in parameter
    /// <c>BuiltInParameter.FAMILY_SHARED</c> on the family. Value <c>1</c> means
    /// the family lives in its own .rfa and is referenceable from any project;
    /// <c>0</c> means it is embedded into the parent (no separate <c>.rfa</c>).
    /// See Autodesk forum thread "How to check if family is shared" and the
    /// Jeremy Tammik Building Coder notes on iterating nested family definitions
    /// in a family document.
    /// </summary>
    /// <param name="familyDoc">An open family document. Caller owns the open-close
    /// cycle — this method MUST NOT close the document.</param>
    /// <param name="ct">Cancellation token.</param>
    private static IReadOnlyList<string> CollectSharedNestedFamilyNames(
        Document familyDoc, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        int instanceCount = 0;
        int sharedCount = 0;

        var collector = new FilteredElementCollector(familyDoc)
            .OfClass(typeof(FamilyInstance));

        foreach (FamilyInstance fi in collector)
        {
            ct.ThrowIfCancellationRequested();
            instanceCount++;

            var family = fi.Symbol?.Family;
            if (family is null) continue;

            if (!IsSharedFamily(family)) continue;
            sharedCount++;

            var name = family.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!seen.Add(name)) continue;

            result.Add(name);
        }

        SmartConLogger.Debug(
            $"Shared-nested scan: {instanceCount} instances, {sharedCount} shared, {result.Count} unique names");
        return result;
    }

    /// <summary>
    /// Returns <c>true</c> if the family lives in its own .rfa and can be
    /// referenced as a shared nested from other documents. Uses
    /// <c>FAMILY_SHARED</c> built-in parameter — the only documented detection
    /// path for shared families in the Revit API.
    /// </summary>
    /// <param name="family">Fully-qualified to avoid clashing with the
    /// <c>SmartCon.Core.Models.FamilyManager</c> namespace imported at the
    /// top of the file (where <c>Family</c> would otherwise be the namespace
    /// itself, not the Revit type).</param>
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
}

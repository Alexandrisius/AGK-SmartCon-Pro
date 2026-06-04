using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

public sealed class RevitFamilyDataExtractionService : IFamilyDataExtractionService
{
    private readonly IRevitContext _revitContext;

    public RevitFamilyDataExtractionService(IRevitContext revitContext)
    {
        _revitContext = revitContext;
    }

    public FamilyExtractionResult Extract(string rfaFilePath, IReadOnlyList<string> expectedParameterNames)
    {
        var doc = _revitContext.GetDocument();
        var app = doc.Application;

        var versionString = _revitContext.GetRevitVersion();
        var revitMajorVersion = int.TryParse(versionString, out var v) ? v : 0;

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
            SmartConLogger.Warn($"Extract failed for '{rfaFilePath}': {ex.Message}");
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
                    SmartConLogger.Warn($"Failed to close family document '{rfaFilePath}': {ex.Message}");
                }

                try
                {
                    Marshal.ReleaseComObject(familyDoc);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Info($"Marshal.ReleaseComObject skipped for '{rfaFilePath}' (Document is not a real COM object in Revit API): {ex.Message}");
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

        var versionString = _revitContext.GetRevitVersion();
        var revitMajorVersion = int.TryParse(versionString, out var v) ? v : 0;

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
            SmartConLogger.Warn($"Extract failed for document '{familyDocument.Title}': {ex.Message}");
            return new FamilyExtractionResult(false, [], null, ex.Message, revitMajorVersion);
        }
    }

    private static FamilyExtractionResult ExtractCore(Document familyDoc, IReadOnlyList<string> expectedParameterNames, int revitMajorVersion)
    {
        var fm = familyDoc.FamilyManager;

        // Extraction summary logging happens at the end (line ~200)

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

        // Parameter extraction happens silently per-type

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
            SmartConLogger.Info($"[Extract] fm.Types.Size=0 — creating temporary type to read parameter values");

            // I-03b: family document — separate Transaction scope, not managed by ITransactionService.
            // We create a temporary type to force Revit to materialize the hidden default parameter values,
            // then immediately roll back so the RFA file is never modified.
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
                    SmartConLogger.Warn($"[Extract] Failed to create temporary type: {ex.Message}");
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

                tx.RollBack(); // Never save the temporary type — RFA remains untouched
            }
        }

        var types = allTypes
            .OrderBy(t => t.TypeName, StringComparer.OrdinalIgnoreCase)
            .Select((t, i) => new FamilyExtractionTypeValues(t.TypeName, i, t.Values))
            .ToList();

        SmartConLogger.Info($"[Extract] RESULT: {types.Count} named types, UntypedValues={(untypedValues is not null ? untypedValues.Count.ToString() : "null")}");

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


}

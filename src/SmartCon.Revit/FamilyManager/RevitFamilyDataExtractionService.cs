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

            if (!familyDoc.IsFamilyDocument)
            {
                return new FamilyExtractionResult(false, [], null, "Not a family document", revitMajorVersion);
            }

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
            var typeIndex = 0;
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
                        familyType.Name, typeIndex++, values));
                }
            }

            var types = allTypes
                .Where(t => !string.IsNullOrWhiteSpace(t.TypeName))
                .OrderBy(t => t.TypeName, StringComparer.OrdinalIgnoreCase)
                .Select((t, i) => new FamilyExtractionTypeValues(t.TypeName, i, t.Values))
                .ToList();

            return new FamilyExtractionResult(true, types, untypedValues, null, revitMajorVersion);
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
            }
        }
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

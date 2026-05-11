using System.Diagnostics;
using System.Globalization;
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
            SmartConLogger.Freeze($"Extract: Starting OpenDocumentFile for '{System.IO.Path.GetFileName(rfaFilePath)}'");
            var swOpen = Stopwatch.StartNew();
            familyDoc = app.OpenDocumentFile(rfaFilePath);
            swOpen.Stop();
            SmartConLogger.Freeze($"Extract: OpenDocumentFile completed in {swOpen.Elapsed.TotalMilliseconds:F1}ms");

            if (!familyDoc.IsFamilyDocument)
            {
                SmartConLogger.Freeze("Extract: Not a family document");
                return new FamilyExtractionResult(false, [], "Not a family document", revitMajorVersion);
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

            SmartConLogger.Freeze($"Extract: Found {paramMap.Count} parameters");

            var allTypes = new List<FamilyExtractionTypeValues>();
            var typeIndex = 0;
            foreach (FamilyType familyType in fm.Types)
            {
                var values = new List<FamilyExtractionValueResult>();

                foreach (var expectedName in expectedParameterNames)
                {
                    var value = ExtractValueForParameter(familyType, expectedName, paramMap);
                    values.Add(value);
                }

                allTypes.Add(new FamilyExtractionTypeValues(
                    familyType.Name, typeIndex++, values));
            }

            var types = allTypes
                .Where(t => !string.IsNullOrWhiteSpace(t.TypeName))
                .OrderBy(t => t.TypeName, StringComparer.OrdinalIgnoreCase)
                .Select((t, i) => new FamilyExtractionTypeValues(t.TypeName, i, t.Values))
                .ToList();

            SmartConLogger.Freeze($"Extract: Extracted {types.Count} types (filtered {allTypes.Count - types.Count} empty)");

            return new FamilyExtractionResult(true, types, null, revitMajorVersion);
        }
        catch (Exception ex)
        {
            SmartConLogger.Freeze($"Extract: Exception - {ex.GetType().Name}: {ex.Message}");
            return new FamilyExtractionResult(false, [], ex.Message, revitMajorVersion);
        }
        finally
        {
            if (familyDoc != null)
            {
                try
                {
                    SmartConLogger.Freeze("Extract: Starting Close");
                    var swClose = Stopwatch.StartNew();
                    familyDoc.Close(false);
                    swClose.Stop();
                    SmartConLogger.Freeze($"Extract: Close completed in {swClose.Elapsed.TotalMilliseconds:F1}ms");

                    // Явное освобождение COM-объекта для форсирования cleanup
                    SmartConLogger.Freeze("Extract: Starting ReleaseComObject");
                    var swRelease = Stopwatch.StartNew();
                    var count = Marshal.ReleaseComObject(familyDoc);
                    swRelease.Stop();
                    SmartConLogger.Freeze($"Extract: ReleaseComObject completed, remaining refs={count}, time={swRelease.Elapsed.TotalMilliseconds:F1}ms");
                }
                catch (Exception ex)
                {
                    SmartConLogger.Freeze($"Extract: Close/Release failed - {ex.GetType().Name}: {ex.Message}");
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
            SmartConLogger.Info($"[FamilyExtract] Parameter '{parameterName}' NOT FOUND in paramMap (checked {paramMap.Count} params)");
            return new FamilyExtractionValueResult(
                parameterName, null, null, null, null, null, null,
                AttributeValueStatus.MissingParameter,
                $"Parameter '{parameterName}' not found in family");
        }

        if (!familyType.HasValue(param))
        {
            SmartConLogger.Info($"[FamilyExtract] Parameter '{parameterName}' FOUND but has no value for type '{familyType.Name}'");
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

            SmartConLogger.Info($"[FamilyExtract] Parameter '{parameterName}' FOUND: valueText='{valueText}', storageType={param.StorageType}");
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

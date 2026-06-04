using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;

namespace SmartCon.Revit.FamilyManager;

public sealed class SystemFamilyAttributeExtractionService : ISystemFamilyAttributeExtractionService
{
    private readonly IRevitUIContext _revitUIContext;
    private readonly IRevitContext _revitContext;

    public SystemFamilyAttributeExtractionService(IRevitUIContext revitUIContext, IRevitContext revitContext)
    {
        _revitUIContext = revitUIContext;
        _revitContext = revitContext;
    }

    public FamilyExtractionResult ExtractFromRvt(string rvtFilePath, IReadOnlyList<string>? typeNames)
    {
        var app = _revitUIContext.GetUIApplication().Application;
        var versionString = _revitContext.GetRevitVersion();
        var revitMajorVersion = int.TryParse(versionString, out var v) ? v : 0;

        Document? rvtDoc = null;
        try
        {
            rvtDoc = app.OpenDocumentFile(rvtFilePath);
            if (rvtDoc is null)
            {
                return new FamilyExtractionResult(false, [], null, "Failed to open rvt document", revitMajorVersion);
            }

            if (rvtDoc.IsFamilyDocument)
            {
                return new FamilyExtractionResult(false, [], null, "Document is a family, not a project", revitMajorVersion);
            }

            return ExtractTypeParametersFromProject(rvtDoc, typeNames, revitMajorVersion);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"[SystemFamilyAttr] Extract failed for '{rvtFilePath}': {ex.Message}");
            return new FamilyExtractionResult(false, [], null, ex.Message, revitMajorVersion);
        }
        finally
        {
            if (rvtDoc != null)
            {
                try
                {
                    rvtDoc.Close(false);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn($"Failed to close rvt '{rvtFilePath}': {ex.Message}");
                }

                try
                {
                    Marshal.ReleaseComObject(rvtDoc);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Info($"Marshal.ReleaseComObject skipped for '{rvtFilePath}' (Document is not a real COM object in Revit API): {ex.Message}");
                }
            }
        }
    }

    private static FamilyExtractionResult ExtractTypeParametersFromProject(
        Document projectDoc,
        IReadOnlyList<string>? requestedTypeNames,
        int revitMajorVersion)
    {
        var requestedSet = requestedTypeNames is { Count: > 0 }
            ? new HashSet<string>(requestedTypeNames, StringComparer.OrdinalIgnoreCase)
            : null;

        var typeCollector = new FilteredElementCollector(projectDoc)
            .OfClass(typeof(ElementType))
            .Cast<ElementType>()
            .Where(et => et.Category is not null)
            .ToList();

        SmartConLogger.Info($"[SystemFamilyAttr] Project contains {typeCollector.Count} element types. Filter: {requestedSet?.Count.ToString() ?? "none"}");

        var types = new List<FamilyExtractionTypeValues>();
        var skippedNoMatch = 0;

        foreach (var elementType in typeCollector)
        {
            var typeName = elementType.Name;
            if (string.IsNullOrWhiteSpace(typeName))
                continue;

            if (requestedSet is not null && !requestedSet.Contains(typeName))
            {
                skippedNoMatch++;
                continue;
            }

            var values = ExtractTypeParameters(elementType);

            types.Add(new FamilyExtractionTypeValues(typeName, 0, values));
        }

        var sorted = types
            .OrderBy(t => t.TypeName, StringComparer.OrdinalIgnoreCase)
            .Select((t, i) => new FamilyExtractionTypeValues(t.TypeName, i, t.Values))
            .ToList();

        if (requestedSet is not null)
        {
            var foundNames = sorted.Select(t => t.TypeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = requestedSet.Except(foundNames).ToList();
            if (missing.Count > 0)
            {
                SmartConLogger.Warn($"[SystemFamilyAttr] {missing.Count} requested types not found in rvt: {string.Join(", ", missing)}");
            }
        }

        SmartConLogger.Info($"[SystemFamilyAttr] Extracted {sorted.Count} types (skipped {skippedNoMatch} non-matching)");

        return new FamilyExtractionResult(true, sorted, null, null, revitMajorVersion);
    }

    private static List<FamilyExtractionValueResult> ExtractTypeParameters(ElementType elementType)
    {
        var result = new List<FamilyExtractionValueResult>();

        foreach (Parameter param in elementType.Parameters)
        {
            if (param.IsReadOnly)
                continue;

            if (param.Definition is null)
                continue;

            var parameterName = param.Definition.Name;
            if (string.IsNullOrWhiteSpace(parameterName))
                continue;

            const AttributeScope scope = AttributeScope.Type;

            FamilyExtractionValueResult value;
            try
            {
                value = param.HasValue
                    ? ReadValue(param)
                    : new FamilyExtractionValueResult(
                        parameterName, scope, param.StorageType.ToString(),
                        null, null, null, null,
                        AttributeValueStatus.EmptyValue, null);
            }
            catch (Exception ex)
            {
                value = new FamilyExtractionValueResult(
                    parameterName, scope, param.StorageType.ToString(),
                    null, null, null, null,
                    AttributeValueStatus.ReadError, ex.Message);
            }

            result.Add(value);
        }

        return result;
    }

    private static FamilyExtractionValueResult ReadValue(Parameter param)
    {
        const AttributeScope scope = AttributeScope.Type;
        var storageType = param.StorageType.ToString();
        string? valueText = null;
        string? valueRaw = null;
        double? valueNumber = null;
        string? unitTypeId = null;

        switch (param.StorageType)
        {
            case StorageType.String:
                valueText = param.AsString();
                valueRaw = valueText;
                break;
            case StorageType.Double:
                var dbl = param.AsDouble();
                valueNumber = dbl;
                valueRaw = FormattableString.Invariant($"{dbl}");
                valueText = param.AsValueString();
                break;
            case StorageType.Integer:
                var intVal = param.AsInteger();
                valueNumber = intVal;
                valueRaw = intVal.ToString();
                valueText = intVal.ToString();
                break;
            case StorageType.ElementId:
                var elemId = param.AsElementId();
                valueText = elemId?.ToString();
                valueRaw = elemId?.ToString();
                break;
            default:
                return new FamilyExtractionValueResult(
                    param.Definition!.Name, scope, storageType,
                    null, null, null, null,
                    AttributeValueStatus.UnsupportedStorageType,
                    $"StorageType '{param.StorageType}' is not supported");
        }

#if REVIT2021_OR_GREATER
        try { unitTypeId = param.GetUnitTypeId()?.TypeId; } catch { }
#endif

        return new FamilyExtractionValueResult(
            param.Definition!.Name, scope, storageType,
            valueText, valueRaw, valueNumber, unitTypeId,
            AttributeValueStatus.Found, null);
    }
}

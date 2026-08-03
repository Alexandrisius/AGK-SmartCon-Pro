using System.IO;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;
using SmartCon.Revit.Util;

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
        var rvtFileName = Path.GetFileName(rvtFilePath);
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

            ParameterUnitDiagnostics.LogDocumentUnits(rvtDoc, "SystemRvt");

            return ExtractTypeParametersFromProject(rvtDoc, typeNames, revitMajorVersion);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"Extract failed for '{rvtFileName}': {ex.Message} [Action: проверьте, что .rvt не повреждён и открывается в Revit вручную]");
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
                    SmartConLogger.Warn($"Failed to close rvt '{rvtFileName}': {ex.Message} [Action: safe to ignore — Revit releases the document on its own]");
                }

                try
                {
                    Marshal.ReleaseComObject(rvtDoc);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug($"Marshal.ReleaseComObject skipped (Document is not a real COM object in Revit API): {ex.Message}");
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

        SmartConLogger.Info($"Project contains {typeCollector.Count} element types. Filter: {requestedSet?.Count.ToString() ?? "none"}");

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

            var values = ExtractTypeParameters(elementType, projectDoc);

            // #191: the family identity must travel with the extraction —
            // without it a keyed family_types row never matches and the
            // save would duplicate/collapse types (see ADR-064). Loadable
            // symbols (FamilySymbol) keep null identity — their family is
            // implied by the catalog item itself.
            types.Add(new FamilyExtractionTypeValues(
                typeName, 0, values,
                elementType is FamilySymbol ? null : elementType.FamilyName,
                elementType is FamilySymbol ? null : SystemFamilyKeyResolver.Resolve(elementType)));
        }

        var sorted = types
            .OrderBy(t => t.TypeName, StringComparer.OrdinalIgnoreCase)
            .Select((t, i) => new FamilyExtractionTypeValues(t.TypeName, i, t.Values, t.FamilyName, t.FamilyKey))
            .ToList();

        if (requestedSet is not null)
        {
            var foundNames = sorted.Select(t => t.TypeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = requestedSet.Except(foundNames).ToList();
            if (missing.Count > 0)
            {
                SmartConLogger.Warn($"{missing.Count} requested types not found in rvt: {string.Join(", ", missing)}");
            }
        }

        SmartConLogger.Info($"Extracted {sorted.Count} types (skipped {skippedNoMatch} non-matching)");

        return new FamilyExtractionResult(true, sorted, null, null, revitMajorVersion);
    }

    private static List<FamilyExtractionValueResult> ExtractTypeParameters(ElementType elementType, Document unitsSource)
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
                    ? ReadValue(param, unitsSource)
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

    private static FamilyExtractionValueResult ReadValue(Parameter param, Document unitsSource)
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
                valueText = Compatibility.RevitUnitsCompat.FormatDisplayValue(unitsSource, param, dbl)
                    ?? Core.Services.Implementation.UnitSymbolFixup.Correct(param.AsValueString());
                ParameterUnitDiagnostics.LogParameterDouble(param, param.Definition?.Name ?? "?", dbl, "SystemRvt");
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


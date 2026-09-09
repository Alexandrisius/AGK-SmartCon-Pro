using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class RevitFamilySnapshotExtractor
{
    private static List<FamilyParameterInfo> ExtractParameters(
        Autodesk.Revit.DB.FamilyManager fm)
    {
        var rawParams = fm.GetParameters();
        SmartConLogger.Debug($"ExtractParameters: fm.GetParameters() returned {rawParams.Count} parameter(s)");
        var result = new List<FamilyParameterInfo>(rawParams.Count);

        foreach (var param in rawParams)
        {
            var name = param.Definition?.Name ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                SmartConLogger.Debug($"  ExtractParameters: skipping param with empty Definition.Name (StorageType={param.StorageType}, IsShared={param.IsShared}, BuiltInId={TryGetBuiltInParameterId(param) ?? "<none>"})");
                continue;
            }

            var storageType = param.StorageType.ToString();
            var group = GetParameterGroup(param);
            var isInstance = param.IsInstance;
            var isShared = param.IsShared;
            var formula = param.Formula;
            var isDeterminedByFormula = param.IsDeterminedByFormula;
            var isReporting = param.IsReporting;
            string? guid = null;
            try
            {
                if (param.IsShared && param.GUID != Guid.Empty)
                    guid = param.GUID.ToString();
            }
            catch { }
            var builtInId = TryGetBuiltInParameterId(param);

            result.Add(new FamilyParameterInfo(
                Name: name,
                StorageType: storageType,
                ParameterGroup: group,
                IsInstance: isInstance,
                IsShared: isShared,
                Formula: formula,
                IsDeterminedByFormula: isDeterminedByFormula,
                IsReporting: isReporting,
                SharedParamGuid: guid,
                BuiltInParameterId: builtInId));
        }

        return result
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.StorageType, StringComparer.Ordinal)
            .ToList();
    }

    private static List<FamilyTypeSnapshot> ExtractTypes(
        Autodesk.Revit.DB.FamilyManager fm, Document familyDoc)
    {
        var totalTypes = fm.Types.Size;
        SmartConLogger.Debug($"ExtractTypes: fm.Types.Size={totalTypes}");
        var paramMap = new Dictionary<string, FamilyParameter>(StringComparer.Ordinal);
        foreach (FamilyParameter param in fm.GetParameters())
        {
            if (param.Definition?.Name is string name)
                paramMap[name] = param;
        }

        // Phase 27: lookup FamilySymbol by type name so we can capture the
        // Revit UniqueId for each type. This mirrors LoadableFamilyTypeResolver
        // (ResolveTypesFromRfa) — the UniqueId is needed by the snapshot-to-DB
        // mapper to populate FamilyTypeDescriptor.UniqueId without re-opening
        // the .rfa in Commit. Collecting it here (in the single Prepare open)
        // eliminates the 42× LoadableResolver.OpenDocumentFile calls seen in
        // the post-import flow.
        var symbolsByName = new FilteredElementCollector(familyDoc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .ToLookup(s => s.Name, s => s, StringComparer.Ordinal);

        var result = new List<FamilyTypeSnapshot>();
        var allTypes = fm.Types.Cast<FamilyType>().ToList();

        foreach (FamilyType familyType in allTypes)
        {
            string typeName;
            if (string.IsNullOrWhiteSpace(familyType.Name))
            {
                // FHV8 (#209): the unnamed default type is ALWAYS skipped in
                // the TYPES section. It is a phantom Revit synthesizes when
                // a typeless family is LOADED into a document: the raw .rfa
                // reports Types.Size=0 while an EditFamily copy of the same
                // family reports Size=1 with this unnamed type — extracting
                // it (formerly as '<default>') made the hash depend on the
                // extraction context (raw open vs post-load copy) and broke
                // import↔migration and file↔nested dedup equality. FHV9:
                // the phantom's VALUES are extracted separately and
                // context-stably by ExtractPhantomTypeValues (PHANTOM
                // section of the identity hash) — the type entry itself
                // stays skipped here. The synthetic
                // FamilyTypeSnapshot.DefaultTypeName constant remains for
                // legacy DB rows and the display rule only.
                SmartConLogger.Debug("  ExtractTypes: skipping unnamed default type (phantom, FHV8)");
                continue;
            }
            else
            {
                typeName = familyType.Name;
            }
            var values = new List<FamilyParameterValue>();

            foreach (var pair in paramMap)
            {
                var value = ExtractParameterValue(familyType, pair.Value, pair.Key, familyDoc);
                values.Add(value);
            }

            var sortedValues = values
                .OrderBy(v => v.ParameterName, StringComparer.Ordinal)
                .ToList();

            var uniqueId = symbolsByName[typeName].FirstOrDefault()?.UniqueId;

            result.Add(new FamilyTypeSnapshot(
                Name: typeName,
                Values: sortedValues,
                UniqueId: uniqueId));
        }

        return result
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static FamilyParameterValue ExtractParameterValue(
        FamilyType familyType,
        FamilyParameter param,
        string parameterName,
        Document familyDoc)
    {
        var storageType = param.StorageType.ToString();

        if (!familyType.HasValue(param))
        {
            return new FamilyParameterValue(
                ParameterName: parameterName,
                StorageType: storageType,
                HasValue: false,
                ValueText: null,
                ValueNumber: null,
                ResolvedElementName: null);
        }

        try
        {
            string? valueText = null;
            double? valueNumber = null;
            string? resolvedName = null;
            string? valueDisplay = null;
            string? specTypeId = null;
            string? unitTypeId = null;

            switch (param.StorageType)
            {
                case StorageType.Double:
                    var dblVal = familyType.AsDouble(param);
                    valueNumber = dblVal;
                    valueText = FormattableString.Invariant($"{dblVal:0.######}");
                    if (dblVal.HasValue)
                    {
                        valueDisplay = Compatibility.RevitUnitsCompat.FormatDisplayValue(familyDoc, param, dblVal.Value);
                        specTypeId = Compatibility.RevitUnitsCompat.GetSpecTypeIdString(param.Definition);
                        unitTypeId = Compatibility.RevitUnitsCompat.GetUnitTypeIdString(param);
                    }
                    break;

                case StorageType.Integer:
                    var intVal = familyType.AsInteger(param);
                    valueNumber = intVal;
                    valueText = intVal.ToString();
                    break;

                case StorageType.String:
                    var strVal = familyType.AsString(param);
                    valueText = strVal ?? string.Empty;
                    break;

                case StorageType.ElementId:
                    var elemId = familyType.AsElementId(param);
                    if (elemId is not null && elemId != ElementId.InvalidElementId)
                    {
                        var elem = familyDoc.GetElement(elemId);
                        resolvedName = elem?.Name;
                        valueText = resolvedName ?? elemId.ToString();
                    }
                    else
                    {
                        valueText = "INVALID";
                    }
                    break;

                default:
                    valueText = "UNSUPPORTED";
                    break;
            }

            return new FamilyParameterValue(
                ParameterName: parameterName,
                StorageType: storageType,
                HasValue: true,
                ValueText: valueText,
                ValueNumber: valueNumber,
                ResolvedElementName: resolvedName,
                ValueDisplay: valueDisplay,
                SpecTypeId: specTypeId,
                UnitTypeId: unitTypeId);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Failed to read parameter '{parameterName}' (StorageType={storageType}): " +
                $"{ex.GetType().Name}: {ex.Message} " +
                "[Action: parameter will be recorded as error value in hash]");
            return new FamilyParameterValue(
                ParameterName: parameterName,
                StorageType: storageType,
                HasValue: true,
                ValueText: "READERROR",
                ValueNumber: null,
                ResolvedElementName: null);
        }
    }

    private static bool IsBlankValue(SystemParameterValue v)
    {
        if (!v.HasValue) return true;
        var text = v.ValueText;
        if (text is null) return false;
        return text.Length == 0
            || text == "INVALID"
            || text == "UNSUPPORTED"
            || text == "READERROR";
    }

    private static bool IsAutoGeneratedName(string parameterName)
    {
        if (string.IsNullOrEmpty(parameterName)) return false;
        return ContainsOrdinalIgnoreCase(parameterName, "IfcGUID")
            || ContainsOrdinalIgnoreCase(parameterName, "IFC GUID");
    }

    private static bool ContainsOrdinalIgnoreCase(string haystack, string needle)
    {
#if NET8_0_OR_GREATER
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
#else
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
#endif
    }

    private static string FormatValuePreview(SystemParameterValue v)
    {
        if (!v.HasValue) return "EMPTY";
        if (v.ValueNumber.HasValue)
            return FormattableString.Invariant($"{v.ValueNumber.Value:0.######}");
        return v.ValueText is null ? "?" : $"\"{v.ValueText}\"";
    }

    private static string GetParameterGroup(FamilyParameter param)
    {
#if REVIT2024_OR_GREATER
        try
        {
            var groupTypeId = param.Definition?.GetGroupTypeId();
            return groupTypeId?.TypeId ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
#else
        try
        {
            return param.Definition?.ParameterGroup.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
#endif
    }

    private static string? TryGetBuiltInParameterId(FamilyParameter param)
    {
        try
        {
            var idValue = param.Id;
#if REVIT2024_OR_GREATER
            var idInt = (int)idValue.Value;
#else
            var idInt = idValue.IntegerValue;
#endif
            if (idInt < 0 && Enum.IsDefined(typeof(BuiltInParameter), idInt))
            {
                return ((BuiltInParameter)idInt).ToString();
            }
        }
        catch
        {
        }
        return null;
    }
}

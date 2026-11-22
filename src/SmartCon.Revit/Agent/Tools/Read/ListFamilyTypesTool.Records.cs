using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed partial class ListFamilyTypesTool
{
    private static Dictionary<string, object?> BuildCompactReport(
        FamilySymbol symbol,
        TypeParameters? typeParameters,
        List<string> fields,
        HashSet<long>? placedTypeIds,
        ref bool parametersTruncated)
    {
        var typeId = symbol.Id.GetValue();
        var report = new Dictionary<string, object?>(fields.Count);
        foreach (var field in fields)
        {
            switch (field)
            {
                case "typeId":
                    report[field] = typeId;
                    break;
                case "name":
                    report[field] = symbol.Name;
                    break;
                case "familyId":
                    report[field] = symbol.Family.Id.GetValue();
                    break;
                case "familyName":
                    report[field] = symbol.Family.Name;
                    break;
                case "isPlaced":
                    report[field] = placedTypeIds!.Contains(typeId);
                    break;
                case "parameters":
                    report[field] = typeParameters!.Names;
                    if (typeParameters.Total > typeParameters.Names.Count)
                        parametersTruncated = true;
                    break;
                case "parameterCount":
                    report[field] = typeParameters!.Total;
                    break;
            }
        }

        return report;
    }

    private static Dictionary<string, object?> BuildFullReport(
        FamilySymbol symbol,
        TypeParameters? typeParameters,
        HashSet<long>? placedTypeIds,
        bool includeParameters)
    {
        var typeId = symbol.Id.GetValue();
        var report = new Dictionary<string, object?>
        {
            ["typeId"] = typeId,
            ["name"] = symbol.Name,
            ["familyId"] = symbol.Family.Id.GetValue(),
            ["familyName"] = symbol.Family.Name
        };
        if (placedTypeIds is not null)
            report["isPlaced"] = placedTypeIds.Contains(typeId);
        if (includeParameters && typeParameters is not null)
        {
            report["parameters"] = typeParameters.Names;
            report["parameterCount"] = typeParameters.Total;
            report["parametersTruncated"] = typeParameters.Total > typeParameters.Names.Count;
        }

        return report;
    }

    private static TypeParameters ReadTypeParameters(
        FamilySymbol symbol,
        int parameterLimit,
        IReadOnlyList<string> requestedNames)
    {
        var result = new TypeParameters();
        foreach (Parameter parameter in symbol.Parameters)
        {
            var name = parameter.Definition?.Name;
            if (string.IsNullOrEmpty(name))
                continue;

            if (result.Names.Count < parameterLimit)
                result.Names.Add(name!);
            if (requestedNames.Count > 0 && !result.Requested.ContainsKey(name!))
            {
                foreach (var requested in requestedNames)
                {
                    if (!string.Equals(requested, name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.Requested.Add(name!, parameter);
                    break;
                }
            }
        }

        result.Total = symbol.Parameters.Size;
        return result;
    }

    /// <summary>
    /// Values of the requested parameters for one type: its own parameters, or —
    /// when <paramref name="instancesByType"/> is set — those of its first placed
    /// instance. Null when the type has none of the requested parameters.
    /// </summary>
    private static Dictionary<string, object?>? ReadValues(
        Document doc,
        IReadOnlyList<string> parameterNames,
        TypeParameters? typeParameters,
        Dictionary<long, List<FamilyInstance>>? instancesByType,
        long typeId,
        ref bool valuesVary)
    {
        if (instancesByType is null)
        {
            if (typeParameters is null || typeParameters.Requested.Count == 0)
                return null;

            var typeValues = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var requested in parameterNames)
            {
                if (typeParameters.Requested.TryGetValue(requested, out var parameter))
                    typeValues[parameter.Definition.Name] = DisplayValue(doc, parameter);
            }

            return typeValues.Count > 0 ? typeValues : null;
        }

        if (!instancesByType.TryGetValue(typeId, out var placed))
            return null;

        placed.Sort((left, right) => left.Id.GetValue().CompareTo(right.Id.GetValue()));
        var first = FindParameters(placed[0], parameterNames);
        if (first is null)
            return null;

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in first)
            values[pair.Key] = DisplayValue(doc, pair.Value);

        for (var index = 1; index < placed.Count && index <= MaxInstancesCheckedForVariance; index++)
        {
            var other = FindParameters(placed[index], parameterNames);
            foreach (var pair in first)
            {
                var otherValue = other is not null && other.TryGetValue(pair.Key, out var parameter)
                    ? DisplayValue(doc, parameter)
                    : null;
                if (!string.Equals((string?)values[pair.Key], otherValue, StringComparison.Ordinal))
                {
                    valuesVary = true;
                    return values;
                }
            }
        }

        return values;
    }

    private static Dictionary<string, Parameter>? FindParameters(
        Element element,
        IReadOnlyList<string> names)
    {
        Dictionary<string, Parameter>? found = null;
        foreach (Parameter parameter in element.Parameters)
        {
            var name = parameter.Definition?.Name;
            if (string.IsNullOrEmpty(name) || found?.ContainsKey(name!) == true)
                continue;

            foreach (var requested in names)
            {
                if (!string.Equals(requested, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                found ??= new Dictionary<string, Parameter>(StringComparer.OrdinalIgnoreCase);
                found.Add(name!, parameter);
                break;
            }
        }

        return found;
    }

    private static string? DisplayValue(Document doc, Parameter parameter)
    {
        if (!parameter.HasValue)
            return null;

        try
        {
            var value = parameter.AsValueString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.AsString() ?? string.Empty;
                case StorageType.Integer:
                    return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.Double:
                    var spec = parameter.Definition?.GetDataType();
                    return spec is null
                        ? null
                        : UnitFormatUtils.Format(doc.GetUnits(), spec, parameter.AsDouble(), false);
                case StorageType.ElementId:
                    return doc.GetElement(parameter.AsElementId())?.Name;
                default:
                    return null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class TypeParameters
    {
        internal List<string> Names { get; } = new();

        internal int Total;

        internal Dictionary<string, Parameter> Requested { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}

using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Read;

internal static class AgentParameterValueReader
{
    internal static ParameterValueInfo Create(
        Document doc,
        Parameter parameter,
        string scope)
    {
        var isShared = false;
        string? guid = null;
        try
        {
            isShared = parameter.IsShared;
            if (isShared)
            {
                var value = parameter.GUID;
                if (value != Guid.Empty)
                    guid = value.ToString("D");
            }
        }
        catch (Exception)
        {
        }

        // An empty parameter (listed with onlyNonEmpty=false) must be told apart
        // from a value that failed to read: without an explicit marker the key
        // disappears and "empty" is indistinguishable from "not returned"
        // (live case 7 round 8).
        var hasValue = true;
        try
        {
            hasValue = parameter.HasValue;
        }
        catch (Exception)
        {
        }

        return new ParameterValueInfo
        {
            Name = parameter.Definition?.Name ?? string.Empty,
            Value = hasValue ? TryReadRawValue(parameter) : string.Empty,
            HasValue = hasValue ? null : false,
            DisplayValue = TryReadDisplayValue(doc, parameter),
            Unit = TryReadUnitLabel(doc, parameter),
            StorageType = parameter.StorageType.ToString(),
            ParameterClass = AgentParameterClassifier.Classify(parameter),
            IsReadOnly = parameter.IsReadOnly,
            ReadOnlyReason = AgentParameterClassifier.ReadOnlyReason(parameter),
            IsShared = isShared,
            ParameterGuid = guid,
            Scope = scope
        };
    }

    private static string? TryReadRawValue(Parameter parameter)
    {
        if (!parameter.HasValue)
            return null;

        try
        {
            return parameter.StorageType switch
            {
                StorageType.String => parameter.AsString(),
                StorageType.Integer => parameter.AsInteger().ToString(CultureInfo.InvariantCulture),
                StorageType.Double => parameter.AsDouble().ToString("R", CultureInfo.InvariantCulture),
                StorageType.ElementId => parameter.AsElementId().GetValue()
                    .ToString(CultureInfo.InvariantCulture),
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryReadDisplayValue(Document doc, Parameter parameter)
    {
        if (!parameter.HasValue)
            return null;

        try
        {
            var value = parameter.AsValueString();
            if (parameter.StorageType != StorageType.Double)
                return string.IsNullOrWhiteSpace(value) ? null : value;

            if (!string.IsNullOrWhiteSpace(value) && !LooksNumeric(value!))
                return value;

            var label = TryReadUnitLabel(doc, parameter);
            if (string.IsNullOrWhiteSpace(label))
                return string.IsNullOrWhiteSpace(value) ? null : value;

            if (string.IsNullOrWhiteSpace(value))
            {
                // AsValueString is normally the only correct display value. When it
                // is empty, formatting AsDouble() (internal feet) with the project
                // unit label produced a number 304.8× too small. Format through
                // Revit instead, and return null when even that fails.
                var dataType = parameter.Definition?.GetDataType();
                if (dataType is null)
                    return null;

                var formatted = UnitFormatUtils.Format(
                    doc.GetUnits(), dataType, parameter.AsDouble(), false);
                return string.IsNullOrWhiteSpace(formatted) ? null : formatted;
            }

            return value + " " + label;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool LooksNumeric(string value)
    {
        foreach (var character in value)
        {
            if (char.IsDigit(character)
                || character is '.' or ',' or '-' or '+' or ' '
                || character == '\u00A0')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string? TryReadUnitLabel(Document doc, Parameter parameter)
    {
        try
        {
            var spec = parameter.Definition?.GetDataType();
            if (spec is null)
                return null;

            var format = doc.GetUnits().GetFormatOptions(spec);
            var unit = format?.GetUnitTypeId();
            if (unit is null)
                return null;

            var label = LabelUtils.GetLabelForUnit(unit);
            return string.IsNullOrWhiteSpace(label) ? null : label;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
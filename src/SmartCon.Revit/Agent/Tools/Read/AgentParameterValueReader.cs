using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Read;

internal static class AgentParameterValueReader
{
    internal static ParameterValueInfo Create(
        Document doc,
        Element owner,
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

        return new ParameterValueInfo
        {
            Name = parameter.Definition?.Name ?? string.Empty,
            Value = TryReadRawValue(parameter),
            DisplayValue = TryReadDisplayValue(doc, parameter),
            Unit = TryReadUnitLabel(doc, parameter),
            StorageType = parameter.StorageType.ToString(),
            ParameterClass = AgentParameterClassifier.Classify(parameter),
            IsReadOnly = parameter.IsReadOnly,
            ReadOnlyReason = AgentParameterClassifier.ReadOnlyReason(parameter, owner),
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

            var number = string.IsNullOrWhiteSpace(value)
                ? parameter.AsDouble().ToString("0.###", CultureInfo.InvariantCulture)
                : value;

            return number + " " + label;
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
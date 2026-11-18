using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Query;

internal static class AgentParameterValue
{
    internal static bool Matches(
        Document doc,
        Parameter parameter,
        QueryCondition condition)
    {
        switch (condition.Op)
        {
            case QueryOperator.Exists:
                return parameter.HasValue;
            case QueryOperator.NotExists:
                return !parameter.HasValue;
        }

        if (!parameter.HasValue || condition.Value is not { } value)
            return false;

        return parameter.StorageType switch
        {
            StorageType.Double => MatchesDouble(doc, parameter, condition.Op, value, condition.Unit),
            StorageType.Integer => MatchesInteger(parameter, condition.Op, value),
            StorageType.ElementId => MatchesElementId(doc, parameter, condition.Op, value),
            _ => MatchesString(parameter.AsString() ?? string.Empty, condition.Op, value)
        };
    }

    internal static (double Value, string? Unit)? ReadMeasured(Document doc, Parameter? parameter)
    {
        if (parameter is null || !parameter.HasValue)
            return null;

        try
        {
            if (parameter.StorageType == StorageType.Integer)
                return (parameter.AsInteger(), null);

            if (parameter.StorageType != StorageType.Double)
                return null;

            var dataType = parameter.Definition.GetDataType();
            if (!UnitUtils.IsMeasurableSpec(dataType))
                return (parameter.AsDouble(), null);

            var projectUnit = doc.GetUnits().GetFormatOptions(dataType).GetUnitTypeId();
            return (
                UnitUtils.ConvertFromInternalUnits(parameter.AsDouble(), projectUnit),
                LabelUtils.GetLabelForUnit(projectUnit));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool MatchesDouble(
        Document doc,
        Parameter parameter,
        QueryOperator op,
        JsonElement value,
        string? unit)
    {
        var actual = parameter.AsDouble();

        if (op is QueryOperator.In or QueryOperator.NotIn)
        {
            if (value.ValueKind != JsonValueKind.Array)
                return false;

            var any = false;
            foreach (var item in value.EnumerateArray())
            {
                if (TryGetNumber(item, out var number)
                    && NearlyEqual(actual, ConvertToInternal(doc, parameter, number, unit)))
                {
                    any = true;
                    break;
                }
            }

            return op == QueryOperator.In ? any : !any;
        }

        if (!TryGetNumber(value, out var expected))
            return false;

        var target = ConvertToInternal(doc, parameter, expected, unit);
        return op switch
        {
            QueryOperator.Eq => NearlyEqual(actual, target),
            QueryOperator.Neq => !NearlyEqual(actual, target),
            QueryOperator.Gt => actual > target,
            QueryOperator.Gte => actual >= target,
            QueryOperator.Lt => actual < target,
            QueryOperator.Lte => actual <= target,
            _ => false
        };
    }

    private static bool MatchesInteger(
        Parameter parameter,
        QueryOperator op,
        JsonElement value)
    {
        if (op is QueryOperator.In or QueryOperator.NotIn)
        {
            if (value.ValueKind != JsonValueKind.Array)
                return false;

            var any = false;
            foreach (var item in value.EnumerateArray())
            {
                if (TryGetNumber(item, out var number)
                    && (int)Math.Round(number) == parameter.AsInteger())
                {
                    any = true;
                    break;
                }
            }

            return op == QueryOperator.In ? any : !any;
        }

        if (!TryGetNumber(value, out var expected))
            return false;

        var actual = parameter.AsInteger();
        var target = (int)Math.Round(expected);
        return op switch
        {
            QueryOperator.Eq => actual == target,
            QueryOperator.Neq => actual != target,
            QueryOperator.Gt => actual > target,
            QueryOperator.Gte => actual >= target,
            QueryOperator.Lt => actual < target,
            QueryOperator.Lte => actual <= target,
            _ => false
        };
    }

    private static bool MatchesElementId(
        Document doc,
        Parameter parameter,
        QueryOperator op,
        JsonElement value)
    {
        var elementId = parameter.AsElementId();
        var actual = elementId.GetValue();

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id))
        {
            return op switch
            {
                QueryOperator.Eq => actual == id,
                QueryOperator.Neq => actual != id,
                _ => MatchElementIdText(doc, parameter, elementId, op, value)
            };
        }

        return MatchElementIdText(doc, parameter, elementId, op, value);
    }

    /// <summary>
    /// String comparison for ElementId parameters. Revit's AsValueString is empty
    /// for Type/Family parameters of instances (tester report C2), so the
    /// referenced element name is used as a fallback.
    /// </summary>
    private static bool MatchElementIdText(
        Document doc,
        Parameter parameter,
        ElementId elementId,
        QueryOperator op,
        JsonElement value)
    {
        var candidates = new List<string>(2);

        try
        {
            var valueString = parameter.AsValueString();
            if (!string.IsNullOrWhiteSpace(valueString))
                candidates.Add(valueString!);
        }
        catch (Exception)
        {
        }

        if (elementId != ElementId.InvalidElementId)
        {
            try
            {
                var element = doc.GetElement(elementId);
                var name = element?.Name;
                if (!string.IsNullOrWhiteSpace(name) && !candidates.Contains(name!))
                    candidates.Add(name!);
            }
            catch (Exception)
            {
            }
        }

        if (candidates.Count == 0)
            return MatchesString(string.Empty, op, value);

        if (op is QueryOperator.In or QueryOperator.NotIn)
        {
            var anyIn = false;
            foreach (var candidate in candidates)
            {
                if (MatchesString(candidate, QueryOperator.In, value))
                {
                    anyIn = true;
                    break;
                }
            }

            return op == QueryOperator.In ? anyIn : !anyIn;
        }

        if (op == QueryOperator.Neq)
        {
            foreach (var candidate in candidates)
            {
                if (!MatchesString(candidate, QueryOperator.Neq, value))
                    return false;
            }

            return true;
        }

        foreach (var candidate in candidates)
        {
            if (MatchesString(candidate, op, value))
                return true;
        }

        return false;
    }

    private static bool MatchesString(
        string actual,
        QueryOperator op,
        JsonElement value)
    {
        if (op is QueryOperator.In or QueryOperator.NotIn)
        {
            if (value.ValueKind != JsonValueKind.Array)
                return false;

            var any = false;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && string.Equals(
                        actual,
                        item.GetString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    any = true;
                    break;
                }
            }

            return op == QueryOperator.In ? any : !any;
        }

        if (value.ValueKind != JsonValueKind.String)
            return false;

        var expected = value.GetString() ?? string.Empty;
        return op switch
        {
            QueryOperator.Eq => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            QueryOperator.Neq => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
#if NET8_0_OR_GREATER
            QueryOperator.Contains => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
#else
            QueryOperator.Contains => actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0,
#endif
            QueryOperator.StartsWith => actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            QueryOperator.EndsWith => actual.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
            QueryOperator.Gt => string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase) > 0,
            QueryOperator.Gte => string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase) >= 0,
            QueryOperator.Lt => string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase) < 0,
            QueryOperator.Lte => string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase) <= 0,
            _ => false
        };
    }

    private static double ConvertToInternal(
        Document doc,
        Parameter parameter,
        double value,
        string? unit)
    {
        if (!string.IsNullOrWhiteSpace(unit) && TryGetUnit(unit!, out var explicitUnit))
            return UnitUtils.ConvertToInternalUnits(value, explicitUnit!);

        var dataType = parameter.Definition.GetDataType();
        if (UnitUtils.IsMeasurableSpec(dataType))
        {
            var projectUnit = doc.GetUnits().GetFormatOptions(dataType).GetUnitTypeId();
            return UnitUtils.ConvertToInternalUnits(value, projectUnit);
        }

        return value;
    }

    private static bool TryGetUnit(string unit, out ForgeTypeId? unitTypeId)
    {
        switch (unit.Trim().ToLowerInvariant())
        {
            case "mm":
            case "millimeter":
            case "millimeters":
                unitTypeId = UnitTypeId.Millimeters;
                return true;
            case "cm":
            case "centimeter":
            case "centimeters":
                unitTypeId = UnitTypeId.Centimeters;
                return true;
            case "m":
            case "meter":
            case "meters":
                unitTypeId = UnitTypeId.Meters;
                return true;
            case "ft":
            case "foot":
            case "feet":
                unitTypeId = UnitTypeId.Feet;
                return true;
            case "in":
            case "inch":
            case "inches":
                unitTypeId = UnitTypeId.Inches;
                return true;
            default:
                unitTypeId = null;
                return false;
        }
    }

    private static bool TryGetNumber(JsonElement value, out double number)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            number = value.GetDouble();
            return true;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number))
        {
            return true;
        }

        number = 0;
        return false;
    }

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= Math.Max(1e-9, Math.Abs(right) * 1e-6);
}
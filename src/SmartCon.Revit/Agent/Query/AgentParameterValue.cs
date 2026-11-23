using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Compatibility;
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
                return CarriesValue(parameter);
            case QueryOperator.NotExists:
                return !CarriesValue(parameter);
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

    /// <summary>
    /// Whether the parameter carries a usable value: no value at all is a hole,
    /// and so is a BLANK string (the Q1 reading "HasValue=true with an empty
    /// string = no value" that AgentParameterResolver.CarriesNoValue applies
    /// before the type lift). exists/notExists must not disagree with the lift
    /// about an element whose only state is a blank own value.
    /// </summary>
    private static bool CarriesValue(Parameter parameter)
    {
        if (!parameter.HasValue)
            return false;

        try
        {
            return parameter.StorageType != StorageType.String
                   || !string.IsNullOrWhiteSpace(parameter.AsString());
        }
        catch (Exception)
        {
            return true;
        }
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

    /// <summary>
    /// String comparison of a text against a condition; shared with the structural
    /// fields (name, family, type, category, level), whose texts are compared
    /// the same way as a text parameter.
    /// </summary>
    internal static bool MatchesString(
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
                if (TryGetTextValue(item, out var candidate)
                    && string.Equals(actual, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    any = true;
                    break;
                }
            }

            return op == QueryOperator.In ? any : !any;
        }

        if (!TryGetTextValue(value, out var expected))
            return false;
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

    /// <summary>
    /// Text form of a condition value. Numeric/boolean scalars are coerced instead of
    /// silently matching nothing: a text parameter (for example a numeric-looking
    /// shared parameter) queried with `gte: 0` used to return an empty set with no
    /// error (benchmark run 2, task 4).
    /// </summary>
    private static bool TryGetTextValue(JsonElement value, out string result)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                result = value.GetString() ?? string.Empty;
                return true;
            case JsonValueKind.Number:
                result = value.GetRawText();
                return true;
            case JsonValueKind.True:
                result = "True";
                return true;
            case JsonValueKind.False:
                result = "False";
                return true;
            default:
                result = string.Empty;
                return false;
        }
    }

    internal static double ConvertToInternal(
        Document doc,
        Parameter parameter,
        double value,
        string? unit)
    {
        var requested = unit?.Trim();
        if (string.Equals(requested, "raw", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requested, "internal", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var dataType = parameter.Definition.GetDataType();
        var measurable = UnitUtils.IsMeasurableSpec(dataType);

        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (!TryGetUnit(requested!, out var explicitUnit) || explicitUnit is null)
            {
                throw new ArgumentException(
                    "Unknown unit '" + requested + "'. Use mm, cm, m, ft, in, m2, m3 or raw " +
                    "(raw = Revit internal units).");
            }

            if (!measurable)
            {
                throw new ArgumentException(
                    "Parameter '" + (parameter.Definition?.Name ?? "?") +
                    "' is dimensionless; omit unit or pass unit=\"raw\".");
            }

            if (!UnitUtils.IsValidUnit(dataType, explicitUnit))
            {
                throw new ArgumentException(
                    "Unit '" + requested + "' does not match parameter '" +
                    (parameter.Definition?.Name ?? "?") + "' (spec " + dataType.TypeId + ").");
            }

            return UnitUtils.ConvertToInternalUnits(value, explicitUnit);
        }

        if (!measurable)
            return value;

        var projectUnit = doc.GetUnits().GetFormatOptions(dataType).GetUnitTypeId();
        return UnitUtils.ConvertToInternalUnits(value, projectUnit);
    }

    /// <summary>Display form of an internal value ("500 мм") for read-back.</summary>
    internal static string? FormatDisplay(Document doc, Parameter parameter, double internalValue)
    {
        try
        {
            return RevitUnitsCompat.FormatDisplayValue(doc, parameter, internalValue);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Display form when only the spec is known (type or project parameter).</summary>
    internal static string? FormatDisplay(Document doc, ForgeTypeId? dataType, double internalValue)
    {
        if (dataType is null)
            return null;

        try
        {
            if (!UnitUtils.IsMeasurableSpec(dataType))
                return null;

            return UnitFormatUtils.Format(doc.GetUnits(), dataType, internalValue, false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static bool TryGetUnit(string unit, out ForgeTypeId? unitTypeId)
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
            case "m2":
            case "sqm":
            case "squaremeter":
            case "squaremeters":
                unitTypeId = UnitTypeId.SquareMeters;
                return true;
            case "m3":
            case "cubicmeter":
            case "cubicmeters":
                unitTypeId = UnitTypeId.CubicMeters;
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
using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Numeric filter rules and unit conversion. Everything goes through the Revit
/// unit API (no conversion factors of our own): UnitFormatUtils parses the project
/// display unit (and a unit suffix in the string), UnitUtils validates and converts
/// an explicit unit.
/// </summary>
internal static partial class AgentFilterRuleBuilder
{
    private static FilterRule? CreateDoubleRule(
        Document doc,
        ElementId parameterId,
        string operation,
        string valueText,
        string? unit,
        ParameterProbe? probe,
        out string? error)
    {
        error = null;
        if (!TryGetInternalValue(doc, parameterId, valueText, unit, probe, out var number, out error))
            return null;

        const double epsilon = 0.0001;
        switch (operation)
        {
            case "eq":
                return ParameterFilterRuleFactory.CreateEqualsRule(parameterId, number, epsilon);
            case "neq":
                return ParameterFilterRuleFactory.CreateNotEqualsRule(parameterId, number, epsilon);
            case "gt":
                return ParameterFilterRuleFactory.CreateGreaterRule(parameterId, number, epsilon);
            case "ge":
                return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(
                    parameterId, number, epsilon);
            case "lt":
                return ParameterFilterRuleFactory.CreateLessRule(parameterId, number, epsilon);
            case "le":
                return ParameterFilterRuleFactory.CreateLessOrEqualRule(
                    parameterId, number, epsilon);
            default:
                error = "Unsupported operator '" + operation + "' for a numeric parameter.";
                return null;
        }
    }

    /// <summary>
    /// Resolves the numeric value in Revit internal units:
    /// 1) raw — as is; 2) explicit unit — convert after dimension validation;
    /// 3) no unit — UnitFormatUtils.TryParse in the parameter's project display unit
    /// (accepts "500", "500 мм", "0.5 m"); 4) numeric fallback + project unit.
    /// A dimensionless parameter with a physical unit is an error, not a silent
    /// 304.8× mistake.
    /// </summary>
    private static bool TryGetInternalValue(
        Document doc,
        ElementId parameterId,
        string valueText,
        string? unit,
        ParameterProbe? probe,
        out double internalValue,
        out string? error)
    {
        internalValue = 0;
        error = null;

        var rawUnit = string.Equals(unit?.Trim(), "raw", StringComparison.OrdinalIgnoreCase);
        var parsedNumber = 0.0;
        if (!rawUnit && !TryParseNumber(valueText, out parsedNumber))
        {
            error = "Parameter is numeric; '" + valueText + "' is not a number.";
            return false;
        }

        if (rawUnit)
            return TryParseNumber(valueText, out internalValue);

        ForgeTypeId? explicitUnit = null;
        if (!string.IsNullOrWhiteSpace(unit)
            && (!AgentParameterValue.TryGetUnit(unit!, out explicitUnit) || explicitUnit is null))
        {
            error = "Unknown unit '" + unit + "'. Use mm, cm, m, ft, in, m2, m3 or raw " +
                    "(raw = Revit internal units).";
            return false;
        }

        var dataType = probe?.DataType;
        var measurable = dataType is not null && UnitUtils.IsMeasurableSpec(dataType);

        if (explicitUnit is not null)
        {
            if (dataType is not null && !measurable)
            {
                error = "Parameter '" + ParameterLabel(parameterId, probe) + "' is dimensionless " +
                        "(spec " + dataType.TypeId + "); omit unit or pass unit=\"raw\".";
                return false;
            }

            if (measurable && !UnitUtils.IsValidUnit(dataType!, explicitUnit))
            {
                error = "Unit '" + unit + "' does not match parameter '" +
                        ParameterLabel(parameterId, probe) + "' (spec " + dataType!.TypeId +
                        "). Use a unit of the same dimension or omit unit to use the project " +
                        "display unit.";
                return false;
            }

            internalValue = UnitUtils.ConvertToInternalUnits(parsedNumber, explicitUnit);
            return true;
        }

        if (measurable)
        {
            // The canonical parser: understands "500", "500 мм", "0.5 m" and applies
            // the project display units of this spec.
            try
            {
                if (UnitFormatUtils.TryParse(doc.GetUnits(), dataType!, valueText, out var parsed))
                {
                    internalValue = parsed;
                    return true;
                }
            }
            catch (Exception)
            {
                // Fall through to the project-unit conversion.
            }
        }

        var number = parsedNumber;
        if (measurable)
        {
            var projectUnit = doc.GetUnits().GetFormatOptions(dataType!).GetUnitTypeId();
            internalValue = UnitUtils.ConvertToInternalUnits(number, projectUnit);
            return true;
        }

        internalValue = number;
        return true;
    }

    private static string ParameterLabel(ElementId parameterId, ParameterProbe? probe) =>
        probe?.Name ?? parameterId.GetValue().ToString(CultureInfo.InvariantCulture);

    internal static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Storage hinted by a spec when no Parameter object is available (project or
    /// shared parameter element): measurable → Double, Number → Double, Integer and
    /// Yes/No → Integer, string/reference by type id.
    /// </summary>
    private static StorageType? StorageFromSpec(ForgeTypeId? spec)
    {
        if (spec is null)
            return null;

        if (UnitUtils.IsMeasurableSpec(spec))
            return StorageType.Double;

        if (spec == SpecTypeId.Number)
            return StorageType.Double;

        if (spec == SpecTypeId.Int.Integer || spec == SpecTypeId.Boolean.YesNo)
            return StorageType.Integer;

        var id = spec.TypeId;
        if (id.IndexOf("string", StringComparison.OrdinalIgnoreCase) >= 0)
            return StorageType.String;

        if (id.IndexOf("reference", StringComparison.OrdinalIgnoreCase) >= 0)
            return StorageType.ElementId;

        return null;
    }
}

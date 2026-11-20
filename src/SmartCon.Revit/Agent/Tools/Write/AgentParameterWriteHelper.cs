using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Resolves and writes element parameters (instance or type scope) with explicit
/// storage-type conversion. Used by set_parameters and remap_parameter_values.
/// </summary>
internal static class AgentParameterWriteHelper
{
    internal sealed class Resolution
    {
        public Parameter? Parameter { get; set; }

        public string? ErrorCode { get; set; }

        public string? Reason { get; set; }

        public bool IsTypeScope { get; set; }

        public bool Ok => Parameter is not null;
    }

    internal static Resolution Resolve(
        Document doc,
        Element element,
        string? name,
        string? guid,
        string? scope)
    {
        var typeScope = string.Equals(scope, "type", StringComparison.OrdinalIgnoreCase)
                        || element is ElementType;

        if (!string.IsNullOrWhiteSpace(guid)
            && Guid.TryParse(guid, out var parsedGuid))
        {
            var byGuid = FindByGuid(doc, element, parsedGuid, typeScope);
            if (byGuid is null)
            {
                return new Resolution
                {
                    ErrorCode = "parameter_not_found",
                    Reason = "No parameter with GUID " + guid + " found on element " + element.Id.GetValue() +
                             (typeScope ? " or its type." : ".")
                };
            }

            return new Resolution { Parameter = byGuid, IsTypeScope = typeScope };
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return new Resolution
            {
                ErrorCode = "invalid_arguments",
                Reason = "Provide the parameter name or GUID."
            };
        }

        var parameter = AgentParameterResolver.Find(doc, element, name!, includeTypeParameters: true);
        if (parameter is null)
        {
            return new Resolution
            {
                ErrorCode = "parameter_not_found",
                Reason = "Parameter '" + name + "' was not found on element " + element.Id.GetValue() +
                         " or its type."
            };
        }

        // A type element owns its parameters directly; a hit there is a type-scope hit.
        var onType = element is ElementType
                     || (parameter.Element is not null
                         && parameter.Element.Id.GetValue() != element.Id.GetValue());
        if (onType && !typeScope)
        {
            return new Resolution
            {
                ErrorCode = "scope_mismatch",
                Reason = "Parameter '" + name + "' belongs to the element type. Pass scope=\"type\" " +
                         "to change it (it affects every instance of the type)."
            };
        }

        if (!onType && typeScope)
        {
            return new Resolution
            {
                ErrorCode = "scope_mismatch",
                Reason = "Parameter '" + name + "' is an instance parameter. Pass scope=\"instance\"."
            };
        }

        return new Resolution { Parameter = parameter, IsTypeScope = typeScope };
    }

    internal static bool TrySet(
        Parameter parameter,
        string? value,
        string? unit,
        out string errorCode,
        out string reason)
    {
        errorCode = string.Empty;
        reason = string.Empty;

        if (parameter.IsReadOnly)
        {
            errorCode = "readonly_parameter";
            reason = "Parameter '" + parameter.Definition?.Name + "' is read-only " +
                     "(it may be driven by a formula or by the element type).";
            return false;
        }

        var text = value ?? string.Empty;

        switch (parameter.StorageType)
        {
            case StorageType.String:
                if (!parameter.Set(text))
                {
                    errorCode = "set_failed";
                    reason = "Revit rejected the value '" + text + "'.";
                    return false;
                }

                return true;

            case StorageType.Integer:
            {
                if (TryParseBool(text, out var flag))
                {
                    if (!parameter.Set(flag ? 1 : 0))
                    {
                        errorCode = "set_failed";
                        reason = "Revit rejected the value '" + text + "'.";
                        return false;
                    }

                    return true;
                }

                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    errorCode = "invalid_value";
                    reason = "'" + text + "' is not a valid integer for parameter '" +
                             parameter.Definition?.Name + "'.";
                    return false;
                }

                return parameter.Set(number) || Fail(out errorCode, out reason, parameter, text);
            }

            case StorageType.Double:
            {
                if (string.IsNullOrWhiteSpace(unit))
                {
                    errorCode = "invalid_value";
                    reason = "Numeric value '" + text + "' for parameter '" +
                             parameter.Definition?.Name + "' needs an explicit unit: for a " +
                             "dimensionless parameter pass unit=\"raw\"; otherwise pass " +
                             "\"mm\", \"cm\", \"m\", \"ft\", \"m2\" or \"m3\".";
                    return false;
                }

                if (!TryValidateUnit(parameter, unit!, out var unitError))
                {
                    errorCode = "invalid_unit";
                    reason = unitError!;
                    return false;
                }

                var (parsed, ok) = ParseDouble(text, unit);
                if (!ok)
                {
                    errorCode = "invalid_value";
                    reason = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        ? "Unit '" + unit + "' is not supported for parameter '" +
                          parameter.Definition?.Name +
                          "'; use mm, cm, m, ft, m2, m3, or raw for a dimensionless parameter."
                        : "'" + text + "' is not a valid number for parameter '" +
                          parameter.Definition?.Name + "'.";
                    return false;
                }

                return parameter.Set(parsed) || Fail(out errorCode, out reason, parameter, text);
            }

            case StorageType.ElementId:
            {
                if (string.IsNullOrWhiteSpace(text)
                    || string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
                {
                    return parameter.Set(ElementId.InvalidElementId)
                           || Fail(out errorCode, out reason, parameter, text);
                }

                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    errorCode = "invalid_value";
                    reason = "'" + text + "' is not a valid element id for parameter '" +
                             parameter.Definition?.Name + "'.";
                    return false;
                }

                return parameter.Set(ElementIdCompat.Create(id))
                       || Fail(out errorCode, out reason, parameter, text);
            }

            default:
                errorCode = "unsupported_storage_type";
                reason = "Parameter '" + parameter.Definition?.Name + "' has storage type " +
                         parameter.StorageType + " and cannot be written.";
                return false;
        }
    }

    internal static string Describe(Parameter parameter)
    {
        try
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.AsString() ?? string.Empty;

                case StorageType.Integer:
                    return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);

                case StorageType.Double:
                    return parameter.AsValueString() ??
                           parameter.AsDouble().ToString("G", CultureInfo.InvariantCulture);

                case StorageType.ElementId:
                    return parameter.AsElementId().GetValue().ToString(CultureInfo.InvariantCulture);

                default:
                    return string.Empty;
            }
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Lossless value representation for change sets (undo). Doubles are stored in
    /// internal units with round-trip formatting, so they can be restored exactly.
    /// </summary>
    internal static string RawValue(Parameter parameter)
    {
        try
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.AsString() ?? string.Empty;

                case StorageType.Integer:
                    return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);

                case StorageType.Double:
                    return parameter.AsDouble().ToString("R", CultureInfo.InvariantCulture);

                case StorageType.ElementId:
                    return parameter.AsElementId().GetValue().ToString(CultureInfo.InvariantCulture);

                default:
                    return string.Empty;
            }
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal static bool TryConvertValue(
        Parameter source,
        Parameter target,
        string? unit,
        out string text,
        out string reason)
    {
        text = string.Empty;
        reason = string.Empty;

        if (source.StorageType == target.StorageType)
        {
            // Same storage type: copy the lossless value (doubles stay in internal units).
            text = RawValue(source);
            return true;
        }

        switch (source.StorageType)
        {
            case StorageType.String:
                text = source.AsString() ?? string.Empty;
                return true;

            case StorageType.Integer:
                text = source.AsInteger().ToString(CultureInfo.InvariantCulture);
                return true;

            case StorageType.Double:
                if (target.StorageType == StorageType.String)
                {
                    text = source.AsValueString() ??
                           source.AsDouble().ToString("G", CultureInfo.InvariantCulture);
                    return true;
                }

                if (target.StorageType == StorageType.Integer)
                {
                    text = ((int)Math.Round(source.AsDouble(), MidpointRounding.AwayFromZero))
                        .ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                reason = "Cannot convert a numeric value into " + target.StorageType + ".";
                return false;

            case StorageType.ElementId:
                if (target.StorageType == StorageType.String)
                {
                    text = source.AsElementId().GetValue().ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                reason = "Cannot convert an element reference into " + target.StorageType + ".";
                return false;

            default:
                reason = "Source storage type " + source.StorageType + " is not supported.";
                return false;
        }
    }

    private static bool Fail(
        out string errorCode,
        out string reason,
        Parameter parameter,
        string value)
    {
        errorCode = "set_failed";
        reason = "Revit rejected the value '" + value + "' for parameter '" +
                 parameter.Definition?.Name + "'.";
        return false;
    }

    private static bool TryParseBool(string text, out bool value)
    {
        value = false;
        if (bool.TryParse(text, out var parsed))
        {
            value = parsed;
            return true;
        }

        if (string.Equals(text, "1", StringComparison.Ordinal))
        {
            value = true;
            return true;
        }

        if (string.Equals(text, "0", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Rejects a unit of the wrong dimension (m2 for Length) and a physical unit on a
    /// dimensionless parameter. The conversion itself stays in ParseDouble.
    /// </summary>
    private static bool TryValidateUnit(Parameter parameter, string unit, out string? error)
    {
        error = null;
        var requested = unit.Trim();
        if (string.Equals(requested, "raw", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requested, "internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!AgentParameterValue.TryGetUnit(requested, out var unitId) || unitId is null)
        {
            error = "Unknown unit '" + requested + "'. Use mm, cm, m, ft, in, m2, m3 or raw.";
            return false;
        }

        ForgeTypeId? spec = null;
        try
        {
            spec = parameter.Definition?.GetDataType();
        }
        catch (Exception)
        {
            // No spec available: the dimension cannot be checked here.
        }

        if (spec is null)
            return true;

        if (!UnitUtils.IsMeasurableSpec(spec))
        {
            error = "Parameter '" + (parameter.Definition?.Name ?? "?") +
                    "' is dimensionless; omit unit or pass unit=\"raw\".";
            return false;
        }

        if (!UnitUtils.IsValidUnit(spec, unitId))
        {
            error = "Unit '" + requested + "' does not match parameter '" +
                    (parameter.Definition?.Name ?? "?") + "' (spec " + spec.TypeId + ").";
            return false;
        }

        return true;
    }

    private static (double Value, bool Ok) ParseDouble(string text, string? unit)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return (0, false);

        if (string.IsNullOrWhiteSpace(unit))
            return (number, true);

        switch (unit!.Trim().ToLowerInvariant())
        {
            case "mm":
            case "millimeter":
            case "millimeters":
                return (UnitUtils.ConvertToInternalUnits(number, UnitTypeId.Millimeters), true);

            case "cm":
                return (UnitUtils.ConvertToInternalUnits(number, UnitTypeId.Centimeters), true);

            case "m":
                return (UnitUtils.ConvertToInternalUnits(number, UnitTypeId.Meters), true);

            case "ft":
            case "foot":
            case "feet":
            case "raw":
            case "internal":
                return (number, true);

            case "in":
            case "inch":
            case "inches":
                return (UnitUtils.ConvertToInternalUnits(number, UnitTypeId.Inches), true);

            case "m2":
            case "sqm":
                return (UnitUtils.ConvertToInternalUnits(number, UnitTypeId.SquareMeters), true);

            case "m3":
                return (UnitUtils.ConvertToInternalUnits(number, UnitTypeId.CubicMeters), true);

            default:
                return (0, false);
        }
    }

    private static Parameter? FindByGuid(
        Document doc,
        Element element,
        Guid guid,
        bool includeTypeParameters)
    {
        foreach (Parameter parameter in element.Parameters)
        {
            if (parameter.GUID == guid)
                return parameter;
        }

        if (!includeTypeParameters)
            return null;

        var typeId = element.GetTypeId();
        if (typeId == ElementId.InvalidElementId)
            return null;

        var type = doc.GetElement(typeId);
        if (type is null)
            return null;

        foreach (Parameter parameter in type.Parameters)
        {
            if (parameter.GUID == guid)
                return parameter;
        }

        return null;
    }
}

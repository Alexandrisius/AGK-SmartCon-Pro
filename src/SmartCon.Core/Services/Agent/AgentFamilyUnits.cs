using System.Globalization;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Storage kind of a family parameter value, resolved from the parameter spec.
/// </summary>
public enum AgentFamilyValueKind
{
    Number = 0,
    Length = 1,
    Angle = 2,
    Area = 3,
    Volume = 4,
    Integer = 5,
    Text = 6,
    YesNo = 7,
    ElementId = 8
}

public readonly struct AgentFamilyConvertedValue
{
    public AgentFamilyValueKind Kind { get; init; }

    public double DoubleValue { get; init; }

    public int IntegerValue { get; init; }

    public string? TextValue { get; init; }

    public long ElementIdValue { get; init; }
}

/// <summary>
/// Pure unit handling for family parameters (no Revit API): numeric values are
/// accepted only with an explicit unit, converted to Revit internal units
/// (feet / radians / square feet / cubic feet).
/// </summary>
public static class AgentFamilyUnits
{
    private const double FeetPerMillimetre = 1.0 / 304.8;
    private const double FeetPerCentimetre = 1.0 / 30.48;
    private const double FeetPerMetre = 3.280839895013123;
    private const double FeetPerInch = 1.0 / 12.0;
    private const double SquareFeetPerSquareMillimetre = 1.0 / 92903.04;
    private const double SquareFeetPerSquareCentimetre = 1.0 / 929.0304;
    private const double SquareFeetPerSquareMetre = 10.763910416709722;
    private const double CubicFeetPerCubicMetre = 35.31466672148859;
    private const double CubicFeetPerLitre = 0.03531466672148859;

    public static bool TryConvert(
        AgentFamilyValueKind kind,
        string? raw,
        string? unit,
        out AgentFamilyConvertedValue value,
        out string? errorCode,
        out string? message)
    {
        value = default;
        errorCode = null;
        message = null;

        if (raw is null)
        {
            errorCode = "invalid_value";
            message = "The value is required.";
            return false;
        }

        var text = raw.Trim();
        var normalizedUnit = NormalizeUnit(unit);

        switch (kind)
        {
            case AgentFamilyValueKind.Text:
                if (normalizedUnit != null)
                {
                    errorCode = "invalid_value";
                    message = "A text parameter does not accept a unit.";
                    return false;
                }

                value = new AgentFamilyConvertedValue
                {
                    Kind = kind,
                    TextValue = raw
                };
                return true;

            case AgentFamilyValueKind.YesNo:
                if (normalizedUnit != null)
                {
                    errorCode = "invalid_value";
                    message = "A Yes/No parameter does not accept a unit.";
                    return false;
                }

                if (!TryParseYesNo(text, out var flag))
                {
                    errorCode = "invalid_value";
                    message = "'" + raw + "' is not a Yes/No value. Use true/false, yes/no, " +
                              "да/нет, 1/0.";
                    return false;
                }

                value = new AgentFamilyConvertedValue
                {
                    Kind = kind,
                    IntegerValue = flag
                };
                return true;

            case AgentFamilyValueKind.Integer:
                if (normalizedUnit != null)
                {
                    errorCode = "invalid_value";
                    message = "An integer parameter does not accept a unit.";
                    return false;
                }

                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    errorCode = "invalid_value";
                    message = "'" + raw + "' is not a valid integer.";
                    return false;
                }

                value = new AgentFamilyConvertedValue
                {
                    Kind = kind,
                    IntegerValue = integer
                };
                return true;

            case AgentFamilyValueKind.ElementId:
                if (normalizedUnit != null)
                {
                    errorCode = "invalid_value";
                    message = "An element reference parameter does not accept a unit.";
                    return false;
                }

                if (text.Length == 0
                    || string.Equals(text, "none", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, "нет", StringComparison.OrdinalIgnoreCase))
                {
                    value = new AgentFamilyConvertedValue
                    {
                        Kind = kind,
                        ElementIdValue = -1
                    };
                    return true;
                }

                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var elementId)
                    || elementId < 0)
                {
                    errorCode = "invalid_value";
                    message = "'" + raw + "' is not a valid element id. Use 'none' to clear the reference.";
                    return false;
                }

                value = new AgentFamilyConvertedValue
                {
                    Kind = kind,
                    ElementIdValue = elementId
                };
                return true;
        }

        if (!TryParseDouble(text, out var number))
        {
            errorCode = "invalid_value";
            message = "'" + raw + "' is not a valid number.";
            return false;
        }

        double factor;
        if (kind == AgentFamilyValueKind.Number)
        {
            factor = UnitFactorForNumber(normalizedUnit, out var numberError, out var numberMessage);
            if (factor == 0.0)
            {
                errorCode = numberError;
                message = numberMessage;
                return false;
            }
        }
        else
        {
            factor = kind switch
            {
                AgentFamilyValueKind.Length => LengthFactor(normalizedUnit),
                AgentFamilyValueKind.Angle => AngleFactor(normalizedUnit),
                AgentFamilyValueKind.Area => AreaFactor(normalizedUnit),
                AgentFamilyValueKind.Volume => VolumeFactor(normalizedUnit),
                _ => 0.0
            };

            if (factor == 0.0)
            {
                errorCode = "unit_required";
                message = "A unit is required for this parameter (" + UnitHint(kind) +
                          "); the value '" + raw + "' was not applied.";
                return false;
            }
        }

        value = new AgentFamilyConvertedValue
        {
            Kind = kind,
            DoubleValue = number * factor
        };
        return true;
    }

    private static double UnitFactorForNumber(string? unit, out string? errorCode, out string? message)
    {
        errorCode = null;
        message = null;
        if (unit is null
            || unit == "number"
            || unit == "число"
            || unit == "шт"
            || unit == "pcs"
            || unit == "unitless")
        {
            return 1.0;
        }

        errorCode = "invalid_value";
        message = "A dimensionless number parameter does not accept the unit '" + unit + "'.";
        return 0.0;
    }

    private static double LengthFactor(string? unit) => unit switch
    {
        "mm" or "мм" => FeetPerMillimetre,
        "cm" or "см" => FeetPerCentimetre,
        "m" or "м" or "метр" or "метры" => FeetPerMetre,
        "in" or "inch" or "дюйм" or "дюймы" => FeetPerInch,
        "ft" or "foot" or "фут" or "футы" => 1.0,
        _ => 0.0
    };

    private static double AngleFactor(string? unit) => unit switch
    {
        "deg" or "degree" or "degrees" or "град" or "градус" or "градусы" or "°" => System.Math.PI / 180.0,
        "rad" or "radian" or "рад" or "радиан" => 1.0,
        _ => 0.0
    };

    private static double AreaFactor(string? unit) => unit switch
    {
        "mm2" or "мм2" or "mm²" or "мм²" => SquareFeetPerSquareMillimetre,
        "cm2" or "см2" or "cm²" or "см²" => SquareFeetPerSquareCentimetre,
        "m2" or "м2" or "m²" or "м²" or "кв.м" or "квм" => SquareFeetPerSquareMetre,
        "ft2" or "фут2" or "фут²" => 1.0,
        _ => 0.0
    };

    private static double VolumeFactor(string? unit) => unit switch
    {
        "m3" or "м3" or "m³" or "м³" or "куб.м" or "кубм" => CubicFeetPerCubicMetre,
        "l" or "л" or "литр" or "литры" => CubicFeetPerLitre,
        "ft3" or "фут3" or "фут³" => 1.0,
        _ => 0.0
    };

    private static string UnitHint(AgentFamilyValueKind kind) => kind switch
    {
        AgentFamilyValueKind.Length => "mm, cm, m, in, ft",
        AgentFamilyValueKind.Angle => "deg, rad",
        AgentFamilyValueKind.Area => "mm2, cm2, m2, ft2",
        AgentFamilyValueKind.Volume => "m3, l, ft3",
        _ => "number"
    };

    public static string? NormalizeUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return null;

        var normalized = unit.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return null;

        // Normalize superscripts and the Russian "кв.м"/"куб.м" spelling used in UI.
        normalized = normalized
            .Replace("²", "2")
            .Replace("³", "3")
            .Replace(" ", string.Empty);

        return normalized;
    }

    public static bool TryParseDouble(string raw, out double value)
    {
        value = 0.0;
        var text = raw.Trim();
        if (text.Length == 0)
            return false;

        // Users type "1,5" in Russian UI; accept it unless a dot is already present.
        if (text.IndexOf('.') < 0 && text.IndexOf(',') >= 0)
            text = text.Replace(',', '.');

        return double.TryParse(
            text,
            NumberStyles.Float | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool TryParseYesNo(string raw, out int flag)
    {
        flag = 0;
        var text = raw.Trim().ToLowerInvariant();
        switch (text)
        {
            case "true":
            case "yes":
            case "y":
            case "1":
            case "on":
            case "да":
            case "истина":
            case "вкл":
                flag = 1;
                return true;
            case "false":
            case "no":
            case "n":
            case "0":
            case "off":
            case "нет":
            case "ложь":
            case "выкл":
                flag = 0;
                return true;
            default:
                return false;
        }
    }

    public static string ToInvariant(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    public static string ToInvariant(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    public static string ToInvariant(long value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
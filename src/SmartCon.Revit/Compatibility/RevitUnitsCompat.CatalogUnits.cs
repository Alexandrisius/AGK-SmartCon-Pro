using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Services.Implementation;

namespace SmartCon.Revit.Compatibility;

public static partial class RevitUnitsCompat
{
    // ── Type Catalog (.txt) unit conversion (issue: bake-in unit bug) ──

    /// <summary>
    /// Резолвит unit annotation из Type Catalog header в <c>UnitTypeId</c> (R21+) или
    /// <c>DisplayUnitType</c> (R19-R20). Pure string-normalization вынесена в
    /// <see cref="TypeCatalogUnitAlias.Normalize"/> для unit-тестирования без Revit API.
    /// Здесь только финальный маппинг canonical key → Revit type.
    /// </summary>
#if REVIT2021_OR_GREATER
    public static ForgeTypeId? ResolveSourceUnitTypeId(string unitAnnotation)
#else
    public static DisplayUnitType ResolveSourceDisplayUnitType(string unitAnnotation)
#endif
    {
        if (string.IsNullOrWhiteSpace(unitAnnotation))
            return UndefinedUnit();

        var key = TypeCatalogUnitAlias.Normalize(unitAnnotation);
        if (key is null)
            return UndefinedUnit();

#if REVIT2021_OR_GREATER
        return key switch
        {
            // Length
            "millimeters" => UnitTypeId.Millimeters,
            "centimeters" => UnitTypeId.Centimeters,
            "decimeters" => UnitTypeId.Decimeters,
            "meters" => UnitTypeId.Meters,
            "inches" => UnitTypeId.Inches,
            "feet" => UnitTypeId.Feet,
            // Angle
            "degrees" => UnitTypeId.Degrees,
            "radians" => UnitTypeId.Radians,
            // grads не существует в R2025+ — пропускаем (хотя key вернётся "grads" из Normalize).
            // Area — базовые доступны с R21, дополнительные с R22+
            "square_millimeters" => UnitTypeId.SquareMillimeters,
            "square_meters" => UnitTypeId.SquareMeters,
            "square_feet" => UnitTypeId.SquareFeet,
#if REVIT2022_OR_GREATER
            "square_centimeters" => UnitTypeId.SquareCentimeters,
            "square_inches" => UnitTypeId.SquareInches,
#endif
            // Volume — базовые доступны с R21, дополнительные с R22+
            "cubic_millimeters" => UnitTypeId.CubicMillimeters,
            "cubic_meters" => UnitTypeId.CubicMeters,
            "cubic_feet" => UnitTypeId.CubicFeet,
#if REVIT2022_OR_GREATER
            "cubic_centimeters" => UnitTypeId.CubicCentimeters,
            "cubic_inches" => UnitTypeId.CubicInches,
#endif
            // Power (rare in type catalog, but возможен)
            "watts" => UnitTypeId.Watts,
            "kilowatts" => UnitTypeId.Kilowatts,
            // Electrical current / voltage (защита от шума)
            "amperes" => UnitTypeId.Amperes,
            "volts" => UnitTypeId.Volts,
            _ => null,
        };
#else
        return key switch
        {
            // Length
            "millimeters" => DisplayUnitType.DUT_MILLIMETERS,
            "centimeters" => DisplayUnitType.DUT_CENTIMETERS,
            "decimeters" => DisplayUnitType.DUT_DECIMETERS,
            "meters" => DisplayUnitType.DUT_METERS,
            "inches" => DisplayUnitType.DUT_DECIMAL_INCHES,
            "feet" => DisplayUnitType.DUT_DECIMAL_FEET,
            // Angle
            "degrees" => DisplayUnitType.DUT_DECIMAL_DEGREES,
            "radians" => DisplayUnitType.DUT_RADIANS,
            "grads" => DisplayUnitType.DUT_GRADS,
            // Area
            "square_millimeters" => DisplayUnitType.DUT_SQUARE_MILLIMETERS,
            "square_centimeters" => DisplayUnitType.DUT_SQUARE_CENTIMETERS,
            "square_meters" => DisplayUnitType.DUT_SQUARE_METERS,
            "square_inches" => DisplayUnitType.DUT_SQUARE_INCHES,
            "square_feet" => DisplayUnitType.DUT_SQUARE_FEET,
            // Volume
            "cubic_millimeters" => DisplayUnitType.DUT_CUBIC_MILLIMETERS,
            "cubic_centimeters" => DisplayUnitType.DUT_CUBIC_CENTIMETERS,
            "cubic_meters" => DisplayUnitType.DUT_CUBIC_METERS,
            "cubic_inches" => DisplayUnitType.DUT_CUBIC_INCHES,
            "cubic_feet" => DisplayUnitType.DUT_CUBIC_FEET,
            // Power
            "watts" => DisplayUnitType.DUT_WATTS,
            "kilowatts" => DisplayUnitType.DUT_KILOWATTS,
            // Electrical
            "amperes" => DisplayUnitType.DUT_AMPERES,
            "volts" => DisplayUnitType.DUT_VOLTS,
            _ => DisplayUnitType.DUT_UNDEFINED,
        };
#endif
    }

#if REVIT2021_OR_GREATER
    /// <summary>
    /// Конвертирует значение ячейки Type Catalog (rawValue в единицах unitAnnotation)
    /// в Revit internal units. Если <paramref name="unitAnnotation"/> не распознана —
    /// возвращает <c>null</c> (вызывающий код skip parameter).
    /// </summary>
    /// <param name="rawValue">Распаршенное число из .txt ячейки.</param>
    /// <param name="unitAnnotation">UNITS из header (<c>param##TYPE##UNITS</c>), может быть null/пусто.</param>
    /// <param name="targetSpecTypeId">
    /// Optional: SpecTypeId целевого параметра (<c>SpecTypeId.Length</c>, <c>SpecTypeId.Angle</c>, ...).
    /// Если задан — выполняется валидация, что source unit валиден для spec.
    /// </param>
    public static double? CatalogCellToInternalUnits(
        double rawValue,
        string? unitAnnotation,
        ForgeTypeId? targetSpecTypeId = null)
    {
        if (string.IsNullOrWhiteSpace(unitAnnotation))
            return null;

        var sourceUnit = ResolveSourceUnitTypeId(unitAnnotation!);
        if (sourceUnit is null)
            return null;

        // Валидация: source unit должен быть валиден для target spec (Length/Angle/Area/...).
        // Dimensionless (Number) или неизвестный spec → пропускаем валидацию.
        if (targetSpecTypeId is not null && !targetSpecTypeId.Empty())
        {
            try
            {
                if (!UnitUtils.IsValidUnit(targetSpecTypeId, sourceUnit))
                {
                    return null;
                }
            }
            catch
            {
                // На некоторых custom spec IsValidUnit может бросить — пропускаем валидацию.
            }
        }

        return UnitUtils.ConvertToInternalUnits(rawValue, sourceUnit);
    }

    /// <summary>
    /// Overload для <see cref="FamilyParameter"/>: получает unit type id параметра через
    /// <c>FamilyParameter.GetUnitTypeId()</c> и находит подходящий <c>SpecTypeId</c>
    /// (Length/Angle/Area/...) для валидации совместимости с source unit из .txt.
    /// </summary>
    public static double? CatalogCellToInternalUnits(
        double rawValue,
        string? unitAnnotation,
        FamilyParameter param)
    {
        if (string.IsNullOrWhiteSpace(unitAnnotation))
            return null;

        ForgeTypeId? paramUnitTypeId = null;
        try { paramUnitTypeId = param.GetUnitTypeId(); }
        catch { paramUnitTypeId = null; }

        if (paramUnitTypeId is null || paramUnitTypeId.Empty())
        {
            // Dimensionless параметр (Number) — конвертация не требуется.
            return rawValue;
        }

        // Находим SpecTypeId параметра: Length, Angle, Area, Volume, ...
        ForgeTypeId? targetSpec = null;
        if (UnitUtils.IsValidUnit(SpecTypeId.Length, paramUnitTypeId))
            targetSpec = SpecTypeId.Length;
        else if (UnitUtils.IsValidUnit(SpecTypeId.Angle, paramUnitTypeId))
            targetSpec = SpecTypeId.Angle;
        else if (UnitUtils.IsValidUnit(SpecTypeId.Area, paramUnitTypeId))
            targetSpec = SpecTypeId.Area;
        else if (UnitUtils.IsValidUnit(SpecTypeId.Volume, paramUnitTypeId))
            targetSpec = SpecTypeId.Volume;
        else if (UnitUtils.IsValidUnit(SpecTypeId.Mass, paramUnitTypeId))
            targetSpec = SpecTypeId.Mass;
        else if (UnitUtils.IsValidUnit(SpecTypeId.HvacPower, paramUnitTypeId))
            targetSpec = SpecTypeId.HvacPower;
        // ElectricalCurrent/ElectricalPotential отсутствуют в SpecTypeId R2025 — пропускаем.

        return CatalogCellToInternalUnits(rawValue, unitAnnotation, targetSpec);
    }
#else
    /// <summary>
    /// R19-R20 версия <see cref="CatalogCellToInternalUnits(double, string?, ForgeTypeId?)"/>.
    /// В R19-R20 нет <c>UnitUtils.IsValidUnit(SpecTypeId, UnitTypeId)</c>, поэтому
    /// валидация через эвристику: оба DUT (source и target) должны быть одной категории
    /// (length/angle/area/volume/power).
    /// </summary>
    public static double? CatalogCellToInternalUnits(
        double rawValue,
        string? unitAnnotation,
        DisplayUnitType targetDisplayUnitType = DisplayUnitType.DUT_UNDEFINED)
    {
        if (string.IsNullOrWhiteSpace(unitAnnotation))
            return null;

        var sourceDut = ResolveSourceDisplayUnitType(unitAnnotation!);
        if (sourceDut == DisplayUnitType.DUT_UNDEFINED)
            return null;

        // Validation: source должен быть одной категории с target
        if (targetDisplayUnitType != DisplayUnitType.DUT_UNDEFINED)
        {
            if (!IsSameUnitCategory(sourceDut, targetDisplayUnitType))
                return null;
        }

        return UnitUtils.ConvertToInternalUnits(rawValue, sourceDut);
    }

    /// <summary>
    /// Overload для <see cref="FamilyParameter"/> в R19-R20: получает DisplayUnitType
    /// параметра через <c>param.DisplayUnitType</c> и передаёт его в overload с
    /// <see cref="DisplayUnitType"/> для валидации совместимости с source unit из .txt.
    /// </summary>
    public static double? CatalogCellToInternalUnits(
        double rawValue,
        string? unitAnnotation,
        FamilyParameter param)
    {
        if (string.IsNullOrWhiteSpace(unitAnnotation))
            return null;

        var targetDut = DisplayUnitType.DUT_UNDEFINED;
        try { targetDut = param.DisplayUnitType; }
        catch { targetDut = DisplayUnitType.DUT_UNDEFINED; }

        if (targetDut == DisplayUnitType.DUT_UNDEFINED)
        {
            // Dimensionless параметр — конвертация не требуется, возвращаем raw.
            return rawValue;
        }

        return CatalogCellToInternalUnits(rawValue, unitAnnotation, targetDut);
    }
#endif

    /// <summary>
    /// Конвертирует значение параметра из internal units Revit в canonical:
    /// LENGTH → мм, ANGLE → градусы, иначе — raw (для Number/Integer/нестандартных spec).
    /// </summary>
    private static double ConvertParamDoubleToCanonical(Parameter param, double internalValue)
    {
#if REVIT2021_OR_GREATER
        try
        {
            var unitTypeId = param.GetUnitTypeId();
            if (unitTypeId is null || unitTypeId.Empty())
                return internalValue;

            // Сначала проверяем через IsValidUnit: это устойчиво к тому, что SpecTypeId
            // параметра может быть кастомным ADSK-форком стандартного Length/Angle.
            if (UnitUtils.IsValidUnit(SpecTypeId.Length, unitTypeId))
                return UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Millimeters);
            if (UnitUtils.IsValidUnit(SpecTypeId.Angle, unitTypeId))
                return UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Degrees);

            // Unknown measurable spec — возвращаем в unit отображения (лучшее приближение).
            return UnitUtils.ConvertFromInternalUnits(internalValue, unitTypeId);
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            // Parameter не имеет units (Number / безразмерные).
        }
        return internalValue;
#else
        try
        {
            var dut = param.DisplayUnitType;
            if (IsLengthDisplayUnit(dut))
                return UnitUtils.ConvertFromInternalUnits(internalValue, DisplayUnitType.DUT_MILLIMETERS);
            if (IsAngleDisplayUnit(dut))
                return UnitUtils.ConvertFromInternalUnits(internalValue, DisplayUnitType.DUT_DECIMAL_DEGREES);
            return UnitUtils.ConvertFromInternalUnits(internalValue, dut);
        }
        catch
        {
            return internalValue;
        }
#endif
    }

#if REVIT2021_OR_GREATER
    private static double ConvertBetweenUnits(double value, ForgeTypeId from, ForgeTypeId to)
    {
        // UnitUtils.Convert требует оба unit одной спецификации — это гарантировано
        // тем, что вызывается только после успешного IsValidUnit(spec, from).
        return UnitUtils.Convert(value, from, to);
    }

    private static ForgeTypeId? SafeGetColumnUnitTypeId(FamilySizeTableColumn column)
    {
        try { return column.GetUnitTypeId(); }
        catch { return null; }
    }

    private static ForgeTypeId? UndefinedUnit() => null;
#else
    private static DisplayUnitType SafeGetColumnDisplayUnitType(FamilySizeTableColumn column)
    {
        try { return column.DisplayUnitType; }
        catch { return DisplayUnitType.DUT_UNDEFINED; }
    }

    private static DisplayUnitType UndefinedUnit() => DisplayUnitType.DUT_UNDEFINED;

    private static bool IsSameUnitCategory(DisplayUnitType a, DisplayUnitType b)
    {
        if (a == b) return true;
        if (IsLengthDisplayUnit(a) && IsLengthDisplayUnit(b)) return true;
        if (IsAngleDisplayUnit(a) && IsAngleDisplayUnit(b)) return true;
        return false;
    }

    private static bool IsLengthDisplayUnit(DisplayUnitType dut) => dut is
        DisplayUnitType.DUT_MILLIMETERS
        or DisplayUnitType.DUT_CENTIMETERS
        or DisplayUnitType.DUT_DECIMETERS
        or DisplayUnitType.DUT_METERS
        or DisplayUnitType.DUT_METERS_CENTIMETERS
        or DisplayUnitType.DUT_DECIMAL_INCHES
        or DisplayUnitType.DUT_FRACTIONAL_INCHES
        or DisplayUnitType.DUT_DECIMAL_FEET
        or DisplayUnitType.DUT_FEET_FRACTIONAL_INCHES;

    private static bool IsAngleDisplayUnit(DisplayUnitType dut) => dut is
        DisplayUnitType.DUT_DECIMAL_DEGREES
        or DisplayUnitType.DUT_DEGREES_AND_MINUTES
        or DisplayUnitType.DUT_RADIANS
        or DisplayUnitType.DUT_GRADS;
#endif
}

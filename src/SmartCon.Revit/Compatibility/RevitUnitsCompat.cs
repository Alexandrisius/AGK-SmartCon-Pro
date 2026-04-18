using System.Globalization;
using Autodesk.Revit.DB;

namespace SmartCon.Revit.Compatibility;

/// <summary>
/// Кросс-версионная конверсия значений Parameter и ячеек FamilySizeTable в
/// канонические единицы измерения (миллиметры для LENGTH, градусы для ANGLE).
/// <para>
/// Revit хранит LENGTH/ANGLE во внутренних единицах (футы/радианы), а экспортирует
/// в единицы колонки CSV (обычно мм/градусы, но может быть дюймы/радианы).
/// Чтобы сравнение parameter-value ↔ CSV-cell работало независимо от project units
/// и от настроек колонки CSV, обе стороны нормализуем в canonical (мм/градусы).
/// </para>
/// </summary>
public static class RevitUnitsCompat
{
    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>
    /// Читает значение параметра и возвращает строку в canonical units
    /// (мм для LENGTH, градусы для ANGLE, raw для NUMBER/INTEGER/STRING).
    /// </summary>
    public static string ReadParamValueAsCsvCompatibleString(Parameter param)
    {
        switch (param.StorageType)
        {
            case StorageType.Double:
                var canonicalValue = ConvertParamDoubleToCanonical(param, param.AsDouble());
                return canonicalValue.ToString("F6", CultureInfo.InvariantCulture);

            case StorageType.Integer:
                return param.AsInteger().ToString(CultureInfo.InvariantCulture);

            case StorageType.String:
                return param.AsString() ?? string.Empty;

            case StorageType.ElementId:
                var id = param.AsElementId();
                return id is null ? string.Empty : ElementIdToString(id);

            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Нормализует значение CSV-ячейки, экспортированной Revit в единицах колонки,
    /// в canonical units (мм/градусы) — чтобы совпадать с параметром, прочитанным
    /// через <see cref="ReadParamValueAsCsvCompatibleString(Parameter)"/>.
    /// </summary>
    /// <param name="cellValue">Распаршенное число из CSV-ячейки (в единицах колонки).</param>
    /// <param name="column">Колонка CSV (из <c>FamilySizeTable.GetColumn</c>).</param>
    public static double NormalizeCellToCanonical(double cellValue, FamilySizeTableColumn column)
    {
#if REVIT2021_OR_GREATER
        var unitTypeId = SafeGetColumnUnitTypeId(column);
        if (unitTypeId is null || unitTypeId.Empty())
            return cellValue;

        // Определяем категорию unit через IsValidUnit для известных measurable specs.
        if (UnitUtils.IsValidUnit(SpecTypeId.Length, unitTypeId))
            return ConvertBetweenUnits(cellValue, unitTypeId, UnitTypeId.Millimeters);
        if (UnitUtils.IsValidUnit(SpecTypeId.Angle, unitTypeId))
            return ConvertBetweenUnits(cellValue, unitTypeId, UnitTypeId.Degrees);

        return cellValue;
#else
        var dut = SafeGetColumnDisplayUnitType(column);
        if (IsLengthDisplayUnit(dut))
            return UnitUtils.Convert(cellValue, dut, DisplayUnitType.DUT_MILLIMETERS);
        if (IsAngleDisplayUnit(dut))
            return UnitUtils.Convert(cellValue, dut, DisplayUnitType.DUT_DECIMAL_DEGREES);
        return cellValue;
#endif
    }

    // ── Internal helpers ────────────────────────────────────────────────

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
#else
    private static DisplayUnitType SafeGetColumnDisplayUnitType(FamilySizeTableColumn column)
    {
        try { return column.DisplayUnitType; }
        catch { return DisplayUnitType.DUT_UNDEFINED; }
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

    private static string ElementIdToString(ElementId id)
    {
#if REVIT2024_OR_GREATER
        return id.Value.ToString(CultureInfo.InvariantCulture);
#else
        return id.IntegerValue.ToString(CultureInfo.InvariantCulture);
#endif
    }
}

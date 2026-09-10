using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Services.Implementation;

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
public static partial class RevitUnitsCompat
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

    /// <summary>
    /// Кросс-версионная конверсия метров → internal units Revit (футы).
    /// R21+: <c>UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters)</c>.
    /// R19–R20: <c>UnitUtils.ConvertToInternalUnits(meters, DisplayUnitType.DUT_METERS)</c>.
    /// </summary>
    public static double MetersToInternal(double meters)
    {
#if REVIT2021_OR_GREATER
        return UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);
#else
        return UnitUtils.ConvertToInternalUnits(meters, DisplayUnitType.DUT_METERS);
#endif
    }

    // ── Display formatting (FamilyManager attribute values) ─────────────

    /// <summary>
    /// Форматирует internal-units значение Double-параметра в человекочитаемую
    /// строку по unit-настройкам документа-владельца с принудительным символом
    /// единицы ("300 мм", "16 бар"). R21+: <c>UnitFormatUtils.Format</c> +
    /// <c>FormatValueOptions { AppendUnitSymbol = true }</c> — символ добавляется
    /// даже когда FormatOptions спеки его не задаёт (типично для Length).
    /// R19-R20: конвертация в DisplayUnitType параметра + первый валидный символ
    /// через <c>LabelUtils.GetLabelFor(UnitSymbolType)</c>.
    /// Возвращает <c>null</c>, когда spec не measurable или форматирование
    /// невозможно — вызывающий код обязан fallback'нуться на AsValueString/raw.
    /// </summary>
#if REVIT2021_OR_GREATER
    public static string? FormatDisplayValue(Document unitsSource, FamilyParameter param, double internalValue)
        => FormatDisplayValueCore(unitsSource, GetSpecTypeId(param.Definition), internalValue);

    public static string? FormatDisplayValue(Document unitsSource, Parameter param, double internalValue)
        => FormatDisplayValueCore(unitsSource, GetSpecTypeId(param.Definition), internalValue);
#else
    public static string? FormatDisplayValue(Document unitsSource, FamilyParameter param, double internalValue)
        => FormatDisplayValueLegacy(SafeGetDisplayUnitType(param), internalValue);

    public static string? FormatDisplayValue(Document unitsSource, Parameter param, double internalValue)
        => FormatDisplayValueLegacy(SafeGetDisplayUnitType(param), internalValue);
#endif

#if REVIT2021_OR_GREATER
    /// <summary>
    /// Data type параметра как ForgeTypeId (R21+; кросс-версионно через
    /// рефлексию для R21-R23, см. #153). <c>null</c> при ошибке.
    /// </summary>
    public static ForgeTypeId? GetDataType(Definition? def)
    {
        return GetSpecTypeId(def);
    }
#endif

    /// <summary>
    /// Spec параметра как строка (Forge TypeId на R21+, ParameterType enum name
    /// на R19-R20) для диагностики и персистентности. <c>null</c> при ошибке.
    /// </summary>
    public static string? GetSpecTypeIdString(Definition? def)
    {
#if REVIT2021_OR_GREATER
        return GetSpecTypeId(def)?.TypeId;
#else
        try { return def?.ParameterType.ToString(); }
        catch { return null; }
#endif
    }

    /// <summary>
    /// Display unit параметра как строка (Forge TypeId на R21+, DisplayUnitType
    /// enum name на R19-R20). <c>null</c> при ошибке или отсутствии unit.
    /// </summary>
    public static string? GetUnitTypeIdString(FamilyParameter param)
    {
#if REVIT2021_OR_GREATER
        try { return param.GetUnitTypeId()?.TypeId; }
        catch { return null; }
#else
        try
        {
            var dut = param.DisplayUnitType;
            return dut == DisplayUnitType.DUT_UNDEFINED ? null : dut.ToString();
        }
        catch { return null; }
#endif
    }

    /// <inheritdoc cref="GetUnitTypeIdString(FamilyParameter)"/>
    public static string? GetUnitTypeIdString(Parameter param)
    {
#if REVIT2021_OR_GREATER
        try { return param.GetUnitTypeId()?.TypeId; }
        catch { return null; }
#else
        try
        {
            var dut = param.DisplayUnitType;
            return dut == DisplayUnitType.DUT_UNDEFINED ? null : dut.ToString();
        }
        catch { return null; }
#endif
    }

#if REVIT2021_OR_GREATER
#if REVIT2024_OR_GREATER
    private static ForgeTypeId? GetSpecTypeId(Definition? def)
    {
        if (def is null) return null;
        try
        {
            return def.GetDataType();
        }
        catch { return null; }
    }
#else
    // R21 binary runs on Revit 2021-2023: Definition.GetSpecTypeId() was
    // REMOVED in Revit 2023 and Definition.GetDataType() was only ADDED in
    // 2022 — no single compile-time API covers all three versions, and any
    // direct reference to a missing method poisons the JIT of the caller
    // (MissingMethodException escapes the surrounding try/catch on net48).
    // Resolve via cached reflection instead. See #153.
    private static readonly System.Reflection.MethodInfo? GetDataTypeMethod =
        typeof(Definition).GetMethod("GetDataType", System.Type.EmptyTypes);
    private static readonly System.Reflection.MethodInfo? GetSpecTypeIdMethod =
        typeof(Definition).GetMethod("GetSpecTypeId", System.Type.EmptyTypes);

    private static ForgeTypeId? GetSpecTypeId(Definition? def)
    {
        if (def is null) return null;
        try
        {
            if (GetDataTypeMethod is not null)
                return GetDataTypeMethod.Invoke(def, null) as ForgeTypeId;
            if (GetSpecTypeIdMethod is not null)
                return GetSpecTypeIdMethod.Invoke(def, null) as ForgeTypeId;
        }
        catch { }
        return null;
    }
#endif

    private static string? FormatDisplayValueCore(Document unitsSource, ForgeTypeId? spec, double internalValue)
    {
        if (unitsSource is null || spec is null) return null;
        try
        {
            // UnitFormatUtils.Format throws ArgumentException for non-measurable
            // specs (UnitUtils.IsMeasurableSpec is unavailable on Revit 2021).
            var options = new FormatValueOptions { AppendUnitSymbol = true };
            var formatted = UnitFormatUtils.Format(unitsSource.GetUnits(), spec, internalValue, false, options);
            return UnitSymbolFixup.Correct(formatted);
        }
        catch { return null; }
    }
#else
    private static DisplayUnitType SafeGetDisplayUnitType(FamilyParameter param)
    {
        try { return param.DisplayUnitType; }
        catch { return DisplayUnitType.DUT_UNDEFINED; }
    }

    private static DisplayUnitType SafeGetDisplayUnitType(Parameter param)
    {
        try { return param.DisplayUnitType; }
        catch { return DisplayUnitType.DUT_UNDEFINED; }
    }

    private static string? FormatDisplayValueLegacy(DisplayUnitType dut, double internalValue)
    {
        try
        {
            if (dut == DisplayUnitType.DUT_UNDEFINED) return null;
            var converted = UnitUtils.ConvertFromInternalUnits(internalValue, dut);
            var text = converted.ToString("0.######", CultureInfo.InvariantCulture);
            var symbols = FormatOptions.GetValidUnitSymbols(dut);
            if (symbols is not null && symbols.Count > 0)
            {
                var label = LabelUtils.GetLabelFor(symbols[0]);
                if (!string.IsNullOrEmpty(label))
                    return UnitSymbolFixup.Correct(text + " " + label);
            }
            return text;
        }
        catch { return null; }
    }
#endif

    // ── Internal helpers ────────────────────────────────────────────────

    private static string ElementIdToString(ElementId id)
    {
#if REVIT2024_OR_GREATER
        return id.Value.ToString(CultureInfo.InvariantCulture);
#else
        return id.IntegerValue.ToString(CultureInfo.InvariantCulture);
#endif
    }
}

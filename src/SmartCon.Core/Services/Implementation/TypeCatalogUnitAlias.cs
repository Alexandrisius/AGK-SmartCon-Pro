namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Pure C# маппинг unit annotation из Type Catalog header (.txt) в canonical key.
/// Вынесен из <c>RevitUnitsCompat.ResolveSourceUnitTypeId</c> для unit-тестирования
/// без Revit API dependency (см. <c>docs/testing/unit-conversion-coverage-gaps.md</c>).
/// <para>
/// Canonical key — lowercase plural form, которая мапится в <c>UnitTypeId</c>
/// (R21+) или <c>DisplayUnitType</c> (R19-R20) на стороне Revit-слоя.
/// </para>
/// <para>
/// Поддерживаемые входные алиасы (case-insensitive, trim whitespace):
/// <list type="bullet">
///   <item><b>Length:</b> <c>millimeters</c>/<c>milimeters</c> (sic — Autodesk docs typo)/
///     <c>millimeter</c>/<c>mm</c>, <c>centimeters</c>/<c>centimeter</c>/<c>cm</c>,
///     <c>decimeters</c>/<c>decimeter</c>/<c>dm</c>, <c>meters</c>/<c>meter</c>/<c>m</c>,
///     <c>inches</c>/<c>inch</c>/<c>in</c>, <c>feet</c>/<c>foot</c>/<c>ft</c></item>
///   <item><b>Angle:</b> <c>degrees</c>/<c>degree</c>/<c>decimal_degrees</c>/<c>deg</c>,
///     <c>radians</c>/<c>radian</c>/<c>rad</c>, <c>grads</c>/<c>grad</c> (R19-R20 only)</item>
///   <item><b>Area/Volume/Power/Electrical:</b> см. <see cref="SupportedAliases"/> для полного списка</item>
/// </list>
/// </para>
/// </summary>
public static class TypeCatalogUnitAlias
{
    /// <summary>
    /// Нормализует unit annotation в canonical key (lowercase plural).
    /// Возвращает <c>null</c> для null/whitespace/unknown входа.
    /// </summary>
    /// <remarks>
    /// Pure C# функция, без зависимостей от Revit API. Полностью testable в SmartCon.Tests.
    /// </remarks>
    public static string? Normalize(string? rawAnnotation)
    {
        if (string.IsNullOrWhiteSpace(rawAnnotation))
            return null;

        var key = rawAnnotation!.Trim().ToLowerInvariant();

        return key switch
        {
            // Length — singular → plural + typo + short alias
            "millimeters" or "millimeter" or "milimeters" or "mm" => "millimeters",
            "centimeters" or "centimeter" or "cm" => "centimeters",
            "decimeters" or "decimeter" or "dm" => "decimeters",
            "meters" or "meter" or "m" => "meters",
            "inches" or "inch" or "in" => "inches",
            "feet" or "foot" or "ft" => "feet",
            // Angle
            "degrees" or "degree" or "decimal_degrees" or "deg" => "degrees",
            "radians" or "radian" or "rad" => "radians",
            "grads" or "grad" => "grads",
            // Area
            "square_millimeters" or "square_millimeter" or "sq_mm" => "square_millimeters",
            "square_centimeters" or "square_centimeter" or "sq_cm" => "square_centimeters",
            "square_meters" or "square_meter" or "sq_m" => "square_meters",
            "square_inches" or "square_inch" or "sq_in" => "square_inches",
            "square_feet" or "square_foot" or "sq_ft" => "square_feet",
            // Volume
            "cubic_millimeters" or "cubic_millimeter" or "cu_mm" => "cubic_millimeters",
            "cubic_centimeters" or "cubic_centimeter" or "cu_cm" => "cubic_centimeters",
            "cubic_meters" or "cubic_meter" or "cu_m" => "cubic_meters",
            "cubic_inches" or "cubic_inch" or "cu_in" => "cubic_inches",
            "cubic_feet" or "cubic_foot" or "cu_ft" => "cubic_feet",
            // Power
            "watts" or "watt" or "w" => "watts",
            "kilowatts" or "kilowatt" or "kw" => "kilowatts",
            // Electrical
            "amperes" or "ampere" or "amps" or "amp" or "a" => "amperes",
            "volts" or "volt" or "v" => "volts",
            _ => null,
        };
    }

    /// <summary>
    /// Все поддерживаемые canonical keys. Используется для diagnostics / validation tools.
    /// </summary>
    public static IReadOnlyCollection<string> SupportedAliases { get; } = new[]
    {
        "millimeters", "centimeters", "decimeters", "meters", "inches", "feet",
        "degrees", "radians", "grads",
        "square_millimeters", "square_centimeters", "square_meters", "square_inches", "square_feet",
        "cubic_millimeters", "cubic_centimeters", "cubic_meters", "cubic_inches", "cubic_feet",
        "watts", "kilowatts",
        "amperes", "volts",
    };
}

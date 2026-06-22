namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Один столбец из header Type Catalog (.txt).
/// <para>
/// Revit type catalog поддерживает формат <c>parameter##TYPE##UNITS</c>,
/// где TYPE (например <c>LENGTH</c>, <c>ANGLE</c>) — spec, а UNITS (например
/// <c>MILLIMETERS</c>, <c>FEET</c>, <c>DECIMAL_DEGREES</c>) — конкретная единица
/// в которой записаны значения колонки. Если annotation отсутствует, Revit
/// интерпретирует значения как project display units (I-08: "Internal Units").
/// </para>
/// <para>
/// См. <see href="https://help.autodesk.com/view/RVT/2025/ENU/?guid=GUID-B6CEE6F4-3E5E-44D8-BF00-7E62E78B6B8E">
/// Revit Family Type Catalog specification</see>.
/// </para>
/// </summary>
/// <param name="Name">Имя параметра (часть до первого <c>##</c>).</param>
/// <param name="TypeAnnotation">
/// Спецификация единицы (например <c>LENGTH</c>, <c>ANGLE</c>). Может быть
/// <c>null</c> если в header annotation отсутствует.
/// </param>
/// <param name="UnitAnnotation">
/// Конкретная единица измерения (например <c>MILLIMETERS</c>, <c>FEET</c>).
/// Может быть <c>null</c> если в header annotation отсутствует.
/// </param>
public sealed record TypeCatalogColumn(
    string Name,
    string? TypeAnnotation,
    string? UnitAnnotation)
{
    /// <summary>True если header содержит <c>##TYPE##UNIT</c> annotation.</summary>
    public bool HasUnitAnnotation => !string.IsNullOrEmpty(UnitAnnotation);
}
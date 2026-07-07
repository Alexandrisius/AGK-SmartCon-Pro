namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Результат парсинга Type Catalog (.txt) семейства Revit.
/// </summary>
/// <param name="Columns">
/// Столбцы header каталога. Каждый столбец хранит имя параметра и опциональные
/// annotation <c>##TYPE##UNITS</c>, которые говорят Revit о единицах измерения
/// значений в колонке. См. <see cref="TypeCatalogColumn"/>.
/// </param>
/// <param name="Entries">Записи (типы) из каталога с raw значениями.</param>
public sealed record TypeCatalogParseResult(
    IReadOnlyList<TypeCatalogColumn> Columns,
    IReadOnlyList<TypeCatalogEntry> Entries)
{
    /// <summary>
    /// Backward-compatible helper: имена столбцов в том же порядке, что и <see cref="Columns"/>.
    /// Используется местами, где нужен только список имён (например для formula-variable lookup).
    /// </summary>
    public IReadOnlyList<string> ParameterNames =>
        Columns.Select(c => c.Name).ToList();

    public bool HasEntries => Entries.Count > 0;

    /// <summary>
    /// Возвращает столбец по имени параметра (case-insensitive) или <c>null</c>.
    /// Используется baker'ом для unit conversion перед <c>FamilyManager.Set</c>.
    /// </summary>
    public TypeCatalogColumn? FindColumn(string parameterName)
    {
        return Columns.FirstOrDefault(c =>
            string.Equals(c.Name, parameterName, StringComparison.OrdinalIgnoreCase));
    }
}

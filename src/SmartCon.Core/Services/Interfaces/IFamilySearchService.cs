namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Поиск семейств и типов в активном документе Revit.
/// Все операции выполняются в контексте ExternalEvent (I-01).
/// </summary>
public interface IFamilySearchService
{
    /// <summary>
    /// Найти загруженное семейство по имени.
    /// Возвращает true если семейство найдено.
    /// </summary>
    bool IsFamilyLoaded(string familyName);

    /// <summary>
    /// Получить список имен типоразмеров семейства.
    /// Возвращает пустой список если семейство не найдено.
    /// </summary>
    IReadOnlyList<string> GetFamilyTypeNames(string familyName);

    /// <summary>
    /// Проверить существование типоразмера в семействе.
    /// </summary>
    bool HasFamilyType(string familyName, string typeName);
}

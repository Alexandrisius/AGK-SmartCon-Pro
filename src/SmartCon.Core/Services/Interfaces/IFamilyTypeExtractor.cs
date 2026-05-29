namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Извлечение списка типоразмеров из .rfa файла без постоянной загрузки в проект.
/// Все операции выполняются в контексте ExternalEvent (I-01).
/// </summary>
public interface IFamilyTypeExtractor
{
    /// <summary>
    /// Извлечь имена типоразмеров из файла семейства.
    /// Открывает .rfa как family document — семейство не остается в проекте.
    /// Возвращает пустой список при ошибке.
    /// </summary>
    IReadOnlyList<string> ExtractTypeNamesFromFile(string filePath);
}

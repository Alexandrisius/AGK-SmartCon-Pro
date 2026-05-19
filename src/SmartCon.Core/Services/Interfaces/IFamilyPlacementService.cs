namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Размещение семейств и типоразмеров в проекте.
/// Все операции выполняются в контексте ExternalEvent (I-01).
/// </summary>
public interface IFamilyPlacementService
{
    /// <summary>
    /// Активировать типоразмер и инициировать его размещение (PostRequestForElementTypePlacement).
    /// Если семейство не загружено — загружает его из файла.
    /// Выполняется синхронно внутри ExternalEvent.
    /// </summary>
    void ActivateAndPlaceType(string familyName, string typeName);

    /// <summary>
    /// Загрузить семейство из файла и инициировать размещение.
    /// Выполняется синхронно внутри ExternalEvent.
    /// </summary>
    void LoadAndPlaceFamily(string filePath, string familyName, string? preferredTypeName = null);
}

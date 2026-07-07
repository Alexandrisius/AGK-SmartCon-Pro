namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Размещение семейств и типоразмеров в проекте.
/// Все операции выполняются в контексте ExternalEvent (I-01).
/// </summary>
public interface IFamilyPlacementService
{
    bool ActivateAndPlaceType(string familyName, string typeName);

    void LoadAndPlaceFamily(string filePath, string familyName, string? preferredTypeName = null);

    void LoadAndPlaceSystemType(string catalogItemId, string typeName);
}

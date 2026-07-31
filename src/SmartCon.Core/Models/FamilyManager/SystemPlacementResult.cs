namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Результат <c>ISystemFamilyPlacementService.LoadAndPlaceSystemType</c>.
/// </summary>
public enum SystemPlacementResult
{
    /// <summary>Тип синхронизирован (при необходимости) и размещение активировано.</summary>
    Placed,

    /// <summary>
    /// Тип синхронизирован и загружен в проект, но интерактивное размещение
    /// для его категории невозможно (<c>UIDocument.CanPlaceElementType</c> =
    /// false — например, изоляция требует существующий host). Пользователь
    /// размещает тип вручную штатными средствами Revit.
    /// </summary>
    LoadedManualPlacementRequired,

    /// <summary>Синхронизация или активация размещения не удалась.</summary>
    Failed,
}

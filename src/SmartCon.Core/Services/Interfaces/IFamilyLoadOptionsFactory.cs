namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Фабрика для создания IFamilyLoadOptions (Revit API).
/// Возвращает object чтобы избежать зависимости от Revit API в Core (I-09).
/// </summary>
public interface IFamilyLoadOptionsFactory
{
    /// <summary>
    /// Создать экземпляр IFamilyLoadOptions для LoadFamily.
    /// Возвращает object — каст в Revit-слое.
    /// </summary>
    object CreateLoadOptions();
}

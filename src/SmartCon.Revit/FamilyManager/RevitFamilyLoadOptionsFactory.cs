using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Фабрика для создания RevitFamilyLoadOptions.
/// </summary>
public sealed class RevitFamilyLoadOptionsFactory : IFamilyLoadOptionsFactory
{
    public object CreateLoadOptions() => new RevitFamilyLoadOptions();
}

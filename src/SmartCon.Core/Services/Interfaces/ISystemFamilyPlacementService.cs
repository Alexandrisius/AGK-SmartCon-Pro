namespace SmartCon.Core.Services.Interfaces;

public interface ISystemFamilyPlacementService
{
    void LoadAndPlaceSystemType(string catalogItemId, string typeName, int targetRevitVersion);
}

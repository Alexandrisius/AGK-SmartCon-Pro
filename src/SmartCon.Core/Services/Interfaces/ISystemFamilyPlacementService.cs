namespace SmartCon.Core.Services.Interfaces;

public interface ISystemFamilyPlacementService
{
    /// <summary>
    /// Synchronize the system type with the catalog (when its ES marker is
    /// not current) and activate placement. Returns <c>true</c> when
    /// placement was activated — the caller uses it to prune the stale
    /// snapshot entry (the just-synced type carries a fresh marker).
    /// </summary>
    bool LoadAndPlaceSystemType(string catalogItemId, string typeName, int targetRevitVersion);
}

using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface ISystemFamilyPlacementService
{
    /// <summary>
    /// Synchronize the system type with the catalog (when its ES marker is
    /// not current) and activate placement. The caller uses the result to
    /// prune the stale snapshot entry (the just-synced type carries a fresh
    /// marker) and to phrase the status message: a type whose category
    /// cannot be placed interactively (<c>CanPlaceElementType</c> = false)
    /// returns <see cref="SystemPlacementResult.LoadedManualPlacementRequired"/>
    /// — the type IS in the project, the user places it manually.
    /// </summary>
    SystemPlacementResult LoadAndPlaceSystemType(string catalogItemId, string typeName, int targetRevitVersion);
}

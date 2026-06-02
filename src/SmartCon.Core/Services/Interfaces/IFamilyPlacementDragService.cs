using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Initiates a Revit-native drag-and-drop operation for placing a family type.
/// Implementation lives in Revit layer and calls UIApplication.DoDragDrop.
/// </summary>
public interface IFamilyPlacementDragService
{
    void StartPlacementDrag(FamilyPlacementDragData data);

    /// <summary>
    /// Fired after the drop handler has completed family loading/placement
    /// and recorded usage. Subscribe to refresh UI (e.g. reload tree).
    /// </summary>
    event Action? PlacementCompleted;

    /// <summary>
    /// Fired when the drop handler fails to load the family.
    /// The argument is the error message. Subscribe to show status in UI.
    /// </summary>
    event Action<string>? PlacementFailed;
}

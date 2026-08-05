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
    /// Fired after a SYSTEM type was successfully synchronized during a drop.
    /// Carries the drag payload so the VM can re-evaluate the item badge
    /// per-type (#202 pattern) instead of clearing the whole family badge
    /// (which wiped the stale dots of the item's other types).
    /// </summary>
    event Action<FamilyPlacementDragData>? SystemTypePlaced;

    /// <summary>
    /// Fired when the drop handler fails to load the family.
    /// The argument is the error message. Subscribe to show status in UI.
    /// </summary>
    event Action<string>? PlacementFailed;

    /// <summary>
    /// Fired when the drop handler successfully loads/activates the family.
    /// The argument is the success message. Subscribe to show status in UI.
    /// </summary>
    event Action<string>? PlacementSucceeded;

    /// <summary>
    /// Fired for intermediate status messages during load (e.g. shared family replacement).
    /// The argument is the status message. Subscribe to show status in UI.
    /// </summary>
    event Action<string>? PlacementStatusMessage;

    /// <summary>
    /// Fired when Revit API asks how to load a conflicting shared nested family.
    /// The handler returns the user's choice from a dialog. Subscribers MUST call
    /// the dialog on Revit main thread and block until user decides.
    /// </summary>
    event Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? SharedFamilyDecisionRequested;
}

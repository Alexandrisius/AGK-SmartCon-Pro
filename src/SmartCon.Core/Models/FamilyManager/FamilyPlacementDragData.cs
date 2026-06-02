namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Payload for drag-and-drop family type placement from FamilyManager to Revit canvas.
/// </summary>
public sealed record FamilyPlacementDragData(
    string CatalogItemId,
    string FamilyName,
    string TypeName,
    int TargetRevitVersion,
    bool IsVirtual = false);

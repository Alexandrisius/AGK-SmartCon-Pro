namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Crop rectangle in source-image pixels. Produced by the avatar crop dialog
/// and consumed by <c>IAvatarCropService</c> to render the derived avatar thumbnail.
/// See ADR-047.
/// </summary>
/// <param name="X">Left edge in source pixels.</param>
/// <param name="Y">Top edge in source pixels.</param>
/// <param name="Width">Width in source pixels.</param>
/// <param name="Height">Height in source pixels.</param>
public sealed record ImageCropRect(double X, double Y, double Width, double Height);

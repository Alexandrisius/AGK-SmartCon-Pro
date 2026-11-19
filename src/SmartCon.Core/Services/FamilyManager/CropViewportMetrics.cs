namespace SmartCon.Core.Services.FamilyManager;

/// <summary>
/// Canonical viewport/frame constants for the avatar crop dialog (issue #131,
/// ADR-047). Shared by CropAvatarViewModel and the SmartCon.UI crop behaviors so
/// the behaviors do not reference the concrete view-model type.
/// </summary>
public static class CropViewportMetrics
{
    public const double ViewportWidth = 600;
    public const double ViewportHeight = 450;
    public const double DefaultFrameWidth = 400;
    public const double DefaultFrameHeight = 300;
}
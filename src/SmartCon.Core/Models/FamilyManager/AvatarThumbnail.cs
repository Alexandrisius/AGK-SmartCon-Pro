namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Unified avatar thumbnail dimensions (ADR-047 / issue #131): a single 4:3 PNG
/// shared by the properties-view avatar and the tooltip preview (both 280×210 @ 2×).
/// </summary>
public static class AvatarThumbnail
{
    /// <summary>Unified avatar thumbnail width in pixels (4:3).</summary>
    public const int Width = 560;

    /// <summary>Unified avatar thumbnail height in pixels (4:3).</summary>
    public const int Height = 420;
}

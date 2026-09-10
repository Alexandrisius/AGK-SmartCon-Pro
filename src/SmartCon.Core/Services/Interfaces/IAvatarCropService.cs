using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Renders the unified avatar thumbnail from a source image of any resolution.
/// Implementation lives in SmartCon.FamilyManager (WPF imaging).
/// See ADR-047 and issue #131.
/// </summary>
public interface IAvatarCropService
{
    /// <summary>Read pixel dimensions of an image file without fully decoding it.</summary>
    (int PixelWidth, int PixelHeight) GetImageDimensions(string path);

    /// <summary>
    /// Crop the given source-pixel rectangle from the source image and render it
    /// scaled to the unified avatar thumbnail (<see cref="AvatarThumbnail.Width"/>×<see cref="AvatarThumbnail.Height"/>)
    /// as a PNG file written to <paramref name="outputPath"/>.
    /// </summary>
    void CropToPng(string sourcePath, ImageCropRect sourceRect, string outputPath);
}

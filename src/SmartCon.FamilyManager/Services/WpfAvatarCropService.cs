using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using Int32Rect = System.Windows.Int32Rect;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// WPF-imaging implementation of <see cref="IAvatarCropService"/> (issue #131, ADR-047).
/// Handles source images of any resolution: decoding is capped at
/// <see cref="MaxDecodeWidth"/> pixels wide so very large sources cannot blow up
/// memory; the crop rectangle (given in native source pixels) is rescaled into
/// the decoded pixel space before cropping.
/// </summary>
public sealed class WpfAvatarCropService : IAvatarCropService
{
    private const int MaxDecodeWidth = 4096;

    public (int PixelWidth, int PixelHeight) GetImageDimensions(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    public void CropToPng(string sourcePath, ImageCropRect sourceRect, string outputPath)
    {
        var (nativeWidth, _) = GetImageDimensions(sourcePath);

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.UriSource = new Uri(sourcePath, UriKind.Absolute);
        if (nativeWidth > MaxDecodeWidth)
            bitmap.DecodePixelWidth = MaxDecodeWidth;
        bitmap.EndInit();
        bitmap.Freeze();

        // The crop rect arrives in native source pixels; rescale into decoded pixel space.
        var ratio = (double)bitmap.PixelWidth / nativeWidth;
        var x = Clamp((int)Math.Round(sourceRect.X * ratio), 0, bitmap.PixelWidth - 1);
        var y = Clamp((int)Math.Round(sourceRect.Y * ratio), 0, bitmap.PixelHeight - 1);
        var w = Clamp((int)Math.Round(sourceRect.Width * ratio), 1, bitmap.PixelWidth - x);
        var h = Clamp((int)Math.Round(sourceRect.Height * ratio), 1, bitmap.PixelHeight - y);

        var cropped = new CroppedBitmap(bitmap, new Int32Rect(x, y, w, h));

        // ADR-047 rev 3 (#131): the selection may have any aspect ratio. Render it
        // into the fixed 4:3 output with CONTAIN semantics — the whole selection
        // stays visible (e.g. a narrow vertical strip is shown full-height), the
        // remaining margins are left transparent (PNG alpha).
        var fitScale = System.Math.Min(
            (double)AvatarThumbnail.Width / w,
            (double)AvatarThumbnail.Height / h);
        var destWidth = w * fitScale;
        var destHeight = h * fitScale;
        var destX = (AvatarThumbnail.Width - destWidth) / 2;
        var destY = (AvatarThumbnail.Height - destHeight) / 2;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(cropped, new System.Windows.Rect(destX, destY, destWidth, destHeight));
        }

        var rtb = new RenderTargetBitmap(
            AvatarThumbnail.Width, AvatarThumbnail.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        encoder.Save(fs);
    }

    private static int Clamp(int value, int min, int max)
        => value < min ? min : value > max ? max : value;
}

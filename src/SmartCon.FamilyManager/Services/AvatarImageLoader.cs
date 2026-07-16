using System.IO;
using System.Windows.Media.Imaging;
using SmartCon.Core.Logging;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// Shared loader for avatar images (ADR-047 / issue #131): decodes from disk on
/// every call (IgnoreImageCache) so a re-rendered avatar.png at the SAME path is
/// picked up immediately — a path-string binding cannot express "same path, new
/// content". Used by the properties view and the tooltip.
/// </summary>
internal static class AvatarImageLoader
{
    public static BitmapImage? Load(string path, int decodePixelWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (decodePixelWidth > 0)
                bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"AvatarImageLoader: failed to decode '{Path.GetFileName(path)}': {ex.Message} [Action: проверьте формат файла]");
            return null;
        }
    }
}

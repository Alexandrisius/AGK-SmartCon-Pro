using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using SmartCon.Core.Logging;

namespace SmartCon.UI.Converters;

[ValueConversion(typeof(string), typeof(BitmapImage))]
public sealed class PathToBitmapImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
            return null;

        if (!File.Exists(path))
            return null;

        var decodePixelWidth = 0;
        if (parameter is int px && px > 0)
            decodePixelWidth = px;
        else if (parameter is string s && int.TryParse(s, out var parsed) && parsed > 0)
            decodePixelWidth = parsed;

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
            SmartConLogger.Warn($"PathToBitmapImageConverter: Failed to load image '{Path.GetFileName(path)}': {ex.Message} [Action: check the image file — the preview falls back to empty]");
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

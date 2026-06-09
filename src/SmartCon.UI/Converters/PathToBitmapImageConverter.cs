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

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"PathToBitmapImageConverter: Failed to load image '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

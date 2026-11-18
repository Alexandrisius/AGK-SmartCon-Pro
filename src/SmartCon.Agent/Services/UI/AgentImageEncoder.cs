using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SmartCon.Agent.Services.UI;

public static class AgentImageEncoder
{
    private const int MaxDimension = 2000;

    public static byte[]? FromFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                return null;

            return Encode(decoder.Frames[0]);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static byte[]? Encode(BitmapSource source)
    {
        try
        {
            BitmapSource frame = source;
            var scale = Math.Min(
                1.0,
                (double)MaxDimension / Math.Max(source.PixelWidth, source.PixelHeight));
            if (scale < 1.0)
            {
                frame = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }

            if (frame.Format != PixelFormats.Bgra32)
            {
                frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            }

            frame = FlattenOntoWhite(frame);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Clipboard bitmaps often carry an all-zero alpha channel, which WPF renders
    /// as a blank white/transparent image (the bytes are fine, the preview is
    /// not). Composite every pixel over white and force alpha=255 so the
    /// thumbnail, the full-size preview and the LLM payload show the real
    /// screenshot.
    /// </summary>
    private static BitmapSource FlattenOntoWhite(BitmapSource frame)
    {
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (width <= 0 || height <= 0)
            return frame;

        var stride = width * 4;
        var pixels = new byte[stride * height];
        frame.CopyPixels(pixels, stride, 0);

        var hasAlpha = frame.Format == PixelFormats.Bgra32
                       || frame.Format == PixelFormats.Pbgra32;
        if (!hasAlpha)
        {
            for (var index = 3; index < pixels.Length; index += 4)
                pixels[index] = 255;

            return BitmapSource.Create(
                width, height, frame.DpiX, frame.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        }

        for (var index = 0; index < pixels.Length; index += 4)
        {
            var alpha = pixels[index + 3];
            if (alpha == 255)
                continue;

            var overlay = 255 - alpha;
            pixels[index] = (byte)((pixels[index] * alpha + 255 * overlay) / 255);
            pixels[index + 1] = (byte)((pixels[index + 1] * alpha + 255 * overlay) / 255);
            pixels[index + 2] = (byte)((pixels[index + 2] * alpha + 255 * overlay) / 255);
            pixels[index + 3] = 255;
        }

        return BitmapSource.Create(
            width, height, frame.DpiX, frame.DpiY, PixelFormats.Bgra32, null, pixels, stride);
    }
}
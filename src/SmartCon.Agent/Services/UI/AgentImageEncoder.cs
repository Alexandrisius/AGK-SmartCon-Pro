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

            if (frame.Format != PixelFormats.Bgra32 && frame.Format != PixelFormats.Pbgra32)
            {
                frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            }

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
}
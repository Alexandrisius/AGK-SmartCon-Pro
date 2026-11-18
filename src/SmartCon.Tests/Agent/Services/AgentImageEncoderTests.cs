using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SmartCon.Agent.Services.UI;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentImageEncoderTests
{
    [Fact]
    public void Encode_AllZeroAlphaClipboardBitmap_KeepsRgbAndForcesOpacity()
    {
        // Clipboard DIBs often look like this: real RGB bytes, alpha = 0.
        var pixels = new byte[]
        {
            10, 20, 30, 0,
            40, 50, 60, 0
        };
        var source = BitmapSource.Create(
            2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);

        var encoded = AgentImageEncoder.Encode(source);

        Assert.NotNull(encoded);
        var decoded = Decode(encoded!);
        var result = new byte[8];
        decoded.CopyPixels(result, 8, 0);

        Assert.Equal(255, result[3]);
        Assert.Equal(255, result[7]);
        Assert.Equal(10, result[0]);
        Assert.Equal(20, result[1]);
        Assert.Equal(30, result[2]);
        Assert.Equal(40, result[4]);
        Assert.Equal(50, result[5]);
        Assert.Equal(60, result[6]);
    }

    [Fact]
    public void Encode_RealTransparency_IsCompositedOnWhite()
    {
        var pixels = new byte[]
        {
            0, 0, 0, 0,
            255, 0, 0, 255
        };
        var source = BitmapSource.Create(
            2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);

        var encoded = AgentImageEncoder.Encode(source);
        var decoded = Decode(encoded!);
        var result = new byte[8];
        decoded.CopyPixels(result, 8, 0);

        Assert.Equal(255, result[0]);
        Assert.Equal(255, result[1]);
        Assert.Equal(255, result[2]);
        Assert.Equal(255, result[3]);
        Assert.Equal(255, result[7]);
    }

    private static BitmapSource Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
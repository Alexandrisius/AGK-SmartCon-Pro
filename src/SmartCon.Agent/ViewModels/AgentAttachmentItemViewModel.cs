using System.IO;
using System.Windows.Media.Imaging;
using SmartCon.Agent.Services.UI;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.ViewModels;

public sealed class AgentAttachmentItemViewModel
{
    public AgentAttachmentItemViewModel(string fileName, string mimeType, byte[] pngBytes)
    {
        FileName = fileName;
        MimeType = mimeType;
        var normalized = Normalize(pngBytes);
        PngBytes = normalized;
        Base64Data = Convert.ToBase64String(normalized);
        Preview = TryDecode(normalized);
    }

    public string FileName { get; }

    public string MimeType { get; }

    public byte[] PngBytes { get; }

    public string Base64Data { get; }

    public BitmapImage? Preview { get; }

    public AgentAttachment ToAttachment() => new()
    {
        FileName = FileName,
        MimeType = MimeType,
        Base64Data = Base64Data
    };

    private static byte[] Normalize(byte[] bytes)
    {
        try
        {
            return AgentImageEncoder.Encode(Decode(bytes)) ?? bytes;
        }
        catch (Exception)
        {
            return bytes;
        }
    }

    private static BitmapImage? TryDecode(byte[] bytes)
    {
        try
        {
            return Decode(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static BitmapImage Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
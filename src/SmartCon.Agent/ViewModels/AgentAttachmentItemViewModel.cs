using System.IO;
using System.Windows.Media.Imaging;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.ViewModels;

public sealed class AgentAttachmentItemViewModel
{
    public AgentAttachmentItemViewModel(string fileName, string mimeType, byte[] pngBytes)
    {
        FileName = fileName;
        MimeType = mimeType;
        PngBytes = pngBytes;
        Base64Data = Convert.ToBase64String(pngBytes);
        Preview = CreatePreview(pngBytes);
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

    private static BitmapImage? CreatePreview(byte[] bytes)
    {
        try
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
        catch (Exception)
        {
            return null;
        }
    }
}
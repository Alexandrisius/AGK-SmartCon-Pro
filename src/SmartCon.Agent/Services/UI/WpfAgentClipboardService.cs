using System.Windows;

namespace SmartCon.Agent.Services.UI;

public sealed class WpfAgentClipboardService : IAgentClipboardService
{
    public byte[]? TryGetImagePng()
    {
        try
        {
            if (!Clipboard.ContainsImage())
                return null;

            var image = Clipboard.GetImage();
            return image is null ? null : AgentImageEncoder.Encode(image);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
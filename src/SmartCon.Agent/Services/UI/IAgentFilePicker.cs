namespace SmartCon.Agent.Services.UI;

public interface IAgentFilePicker
{
    IReadOnlyList<string> PickImages();
}

public interface IAgentClipboardService
{
    byte[]? TryGetImagePng();
}
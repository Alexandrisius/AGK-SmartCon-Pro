using System.IO;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Agent.Services.UI;

namespace SmartCon.Agent.ViewModels;

public sealed partial class AgentMainViewModel
{
    private static readonly string[] SupportedImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp"
    };

    [RelayCommand]
    private void AttachFiles()
    {
        var paths = _filePicker.PickImages();
        AddFiles(paths);
    }

    [RelayCommand]
    private void PasteImage()
    {
        var png = _clipboardService.TryGetImagePng();
        if (png is null)
        {
            StatusText = S("Agent_Status_NoImage", "В буфере обмена нет изображения");
            return;
        }

        Attachments.Add(new AgentAttachmentItemViewModel(
            "screenshot-" + DateTime.Now.ToString("HHmmss") + ".png",
            "image/png",
            png));
        StatusText = S("Agent_Status_ScreenshotAttached", "Скриншот прикреплён");
    }

    [RelayCommand]
    private void AddDroppedFiles(string[]? paths)
    {
        AddFiles(paths ?? Array.Empty<string>());
    }

    [RelayCommand]
    private void RemoveAttachment(AgentAttachmentItemViewModel? item)
    {
        if (item is not null)
            Attachments.Remove(item);
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        var skipped = 0;

        foreach (var path in paths)
        {
            if (!IsSupportedImage(path))
            {
                skipped++;
                continue;
            }

            var bytes = AgentImageEncoder.FromFile(path);
            if (bytes is null)
            {
                skipped++;
                continue;
            }

            Attachments.Add(new AgentAttachmentItemViewModel(
                Path.GetFileName(path),
                "image/png",
                bytes));
            added++;
        }

        if (added > 0)
            StatusText = Format("Agent_Status_AttachmentAdded", "Прикреплено: {0}", added.ToString());
        else if (skipped > 0)
            StatusText = S("Agent_Status_Unsupported", "Поддерживаются только PNG, JPG и BMP");
    }

    private static bool IsSupportedImage(string path)
    {
        var extension = Path.GetExtension(path);
        foreach (var supported in SupportedImageExtensions)
        {
            if (string.Equals(extension, supported, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
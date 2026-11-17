using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartCon.Agent.ViewModels;

public sealed partial class AgentMainViewModel
{
    [ObservableProperty]
    private bool _isImagePreviewOpen;

    [ObservableProperty]
    private ImageSource? _previewImage;

    [RelayCommand]
    private void OpenImagePreview(AgentAttachmentItemViewModel? attachment)
    {
        if (attachment?.Preview is null)
            return;

        PreviewImage = attachment.Preview;
        IsImagePreviewOpen = true;
    }

    [RelayCommand]
    private void CloseImagePreview()
    {
        IsImagePreviewOpen = false;
        PreviewImage = null;
    }
}
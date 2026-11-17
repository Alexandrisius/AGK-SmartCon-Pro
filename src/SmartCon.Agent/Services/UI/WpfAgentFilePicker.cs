using Microsoft.Win32;

namespace SmartCon.Agent.Services.UI;

public sealed class WpfAgentFilePicker : IAgentFilePicker
{
    public IReadOnlyList<string> PickImages()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Прикрепить изображение",
            Filter = "Изображения (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            Multiselect = true,
            CheckFileExists = true
        };

        return dialog.ShowDialog() == true
            ? dialog.FileNames
            : Array.Empty<string>();
    }
}
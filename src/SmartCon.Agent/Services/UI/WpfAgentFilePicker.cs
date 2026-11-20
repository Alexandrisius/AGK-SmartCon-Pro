using Microsoft.Win32;
using SmartCon.UI;

namespace SmartCon.Agent.Services.UI;

public sealed class WpfAgentFilePicker : IAgentFilePicker
{
    public IReadOnlyList<string> PickImages()
    {
        var dialog = new OpenFileDialog
        {
            Title = LanguageManager.GetString("Agent_ImagePicker_Title") ?? "Attach image",
            Filter = LanguageManager.GetString("Agent_ImagePicker_Filter")
                     ?? "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            Multiselect = true,
            CheckFileExists = true
        };

        return dialog.ShowDialog() == true
            ? dialog.FileNames
            : Array.Empty<string>();
    }
}
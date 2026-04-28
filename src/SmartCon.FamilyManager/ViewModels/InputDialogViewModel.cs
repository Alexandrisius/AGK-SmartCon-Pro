using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class InputDialogViewModel : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _prompt = string.Empty;
    [ObservableProperty] private string _inputText = string.Empty;

    [RelayCommand]
    private void Ok(System.Windows.Window window)
    {
        window.DialogResult = true;
        window.Close();
    }

    [RelayCommand]
    private void Cancel(System.Windows.Window window)
    {
        window.DialogResult = false;
        window.Close();
    }
}

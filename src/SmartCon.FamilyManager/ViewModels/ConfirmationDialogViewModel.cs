using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class ConfirmationDialogViewModel : ObservableObject, IObservableRequestClose
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _message = string.Empty;

    public event Action<bool?>? RequestClose;

    [RelayCommand]
    private void Yes() => RequestClose?.Invoke(true);

    [RelayCommand]
    private void No() => RequestClose?.Invoke(false);
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// ViewModel for the generic input dialog.
/// </summary>
public sealed partial class InputDialogViewModel : ObservableObject, IObservableRequestClose
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _prompt = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty] private string _placeholderText = string.Empty;

    public event Action<bool?>? RequestClose;

    [RelayCommand(CanExecute = nameof(HasInputText))]
    private void Ok() => RequestClose?.Invoke(true);

    private bool HasInputText => !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(null);
}

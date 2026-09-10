using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class ConfirmationDialogViewModel : ObservableObject, IObservableRequestClose
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _message = string.Empty;

    /// <summary>
    /// OK-only mode (informational dialogs): the "Нет" button is hidden
    /// and the confirm button shows <see cref="YesText"/> ("Понятно").
    /// </summary>
    [ObservableProperty] private bool _isOkOnly;

    /// <summary>Confirm-button text override (null → localized "Да").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmText))]
    private string? _yesText;

    /// <summary>Effective confirm-button text (never null, I-09-safe localization via UI layer).</summary>
    public string ConfirmText => YesText
        ?? LanguageManager.GetString(StringLocalization.Keys.Btn_Yes)
        ?? "Да";

    public event Action<bool?>? RequestClose;

    [RelayCommand]
    private void Yes() => RequestClose?.Invoke(true);

    [RelayCommand]
    private void No() => RequestClose?.Invoke(false);
}

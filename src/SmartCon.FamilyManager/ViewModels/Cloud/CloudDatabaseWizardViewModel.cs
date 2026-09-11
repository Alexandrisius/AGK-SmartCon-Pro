using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels.Cloud;

/// <summary>
/// Диалог «Создать облачную базу» (§7.3.1, режим б): серверный каталог +
/// CloudLink без публикации контента. Подключение по приглашению живёт в
/// «Подключить базу» (<see cref="ConnectDatabaseViewModel"/>) — решение
/// владельца 2026-09-11: создание и подключение не смешиваются в одном
/// диалоге. Диалог только собирает имя; оркестрация (шаг аккаунта, сеть,
/// регистрация копии) — в FamilyManagerMainViewModel.Cloud.cs.
/// </summary>
public sealed partial class CloudDatabaseWizardViewModel : ObservableObject, SmartCon.Core.Services.Interfaces.IObservableRequestClose
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private string _databaseName = string.Empty;

    public bool Accepted { get; private set; }

    public event Action<bool?>? RequestClose;

    private bool CanOk => !string.IsNullOrWhiteSpace(DatabaseName);

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        Accepted = true;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(null);
}

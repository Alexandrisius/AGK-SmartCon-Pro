using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels.Cloud;

/// <summary>
/// Диалог «Облачная база…» (§7.3.1): два пути мастера — «пустая облачная
/// база» (режим б: серверный каталог + CloudLink без публикации контента)
/// и «подключиться по приглашению». Диалог только собирает намерение;
/// оркестрация (шаг аккаунта, сеть, регистрация копии) — в
/// FamilyManagerMainViewModel.Cloud.cs.
/// </summary>
public sealed partial class CloudDatabaseWizardViewModel : ObservableObject, SmartCon.Core.Services.Interfaces.IObservableRequestClose
{
    public enum WizardMode
    {
        CreateEmpty,
        SubscribeByInvite,
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreateMode))]
    [NotifyPropertyChangedFor(nameof(IsSubscribeMode))]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private WizardMode _mode = WizardMode.CreateEmpty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private string _databaseName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInviteError))]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private string _inviteString = string.Empty;

    public bool Accepted { get; private set; }

    /// <summary>Распарсенное приглашение (валидно только в режиме подписки).</summary>
    public CloudInvite.InviteData? Invite => CloudInvite.TryParse(InviteString);

    /// <summary>
    /// RadioButton-обёртки с СЕТТЕРАМИ: TwoWay-биндинг на get-only свойство
    /// умирает при инициализации (ни одна кнопка не выбрана) и не пушит
    /// клики в Mode. Set по клику меняет Mode; галку снимает сама группа.
    /// </summary>
    public bool IsCreateMode
    {
        get => Mode == WizardMode.CreateEmpty;
        set
        {
            if (value) Mode = WizardMode.CreateEmpty;
        }
    }

    public bool IsSubscribeMode
    {
        get => Mode == WizardMode.SubscribeByInvite;
        set
        {
            if (value) Mode = WizardMode.SubscribeByInvite;
        }
    }

    public bool HasInviteError => IsSubscribeMode
        && !string.IsNullOrWhiteSpace(InviteString)
        && Invite is null;

    public event Action<bool?>? RequestClose;

    private bool CanOk => Mode == WizardMode.CreateEmpty
        ? !string.IsNullOrWhiteSpace(DatabaseName)
        : Invite is not null;

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        Accepted = true;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(null);
}

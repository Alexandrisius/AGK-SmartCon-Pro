using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.FamilyManager.Services.Cloud;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Диалог «Подключить базу»: радио «локальная база из папки» / «облачная по
/// строке приглашения» (решение владельца 2026-09-11 — подключение по
/// приглашению живёт здесь, а не в мастере создания). Диалог только собирает
/// намерение; оркестрация — FamilyManagerMainViewModel.Database.cs / .Cloud.cs.
/// </summary>
public sealed partial class ConnectDatabaseViewModel : ObservableObject, SmartCon.Core.Services.Interfaces.IObservableRequestClose
{
    private readonly Func<string?>? _folderBrowser;

    public enum ConnectMode
    {
        LocalFolder,
        CloudInvite,
    }

    public ConnectDatabaseViewModel(Func<string?>? folderBrowser = null)
    {
        _folderBrowser = folderBrowser;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalMode))]
    [NotifyPropertyChangedFor(nameof(IsCloudMode))]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private ConnectMode _mode = ConnectMode.LocalFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInviteError))]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private string _inviteString = string.Empty;

    public bool Accepted { get; private set; }

    /// <summary>Распарсенное приглашение (валидно только в облачном режиме).</summary>
    public CloudInvite.InviteData? Invite => CloudInvite.TryParse(InviteString);

    /// <summary>
    /// RadioButton-обёртки с СЕТТЕРАМИ: TwoWay-биндинг на get-only свойство
    /// умирает при инициализации (ни одна кнопка не выбрана) и не пушит
    /// клики в Mode. Set по клику меняет Mode; галку снимает сама группа.
    /// </summary>
    public bool IsLocalMode
    {
        get => Mode == ConnectMode.LocalFolder;
        set
        {
            if (value) Mode = ConnectMode.LocalFolder;
        }
    }

    public bool IsCloudMode
    {
        get => Mode == ConnectMode.CloudInvite;
        set
        {
            if (value) Mode = ConnectMode.CloudInvite;
        }
    }

    public bool HasInviteError => IsCloudMode
        && !string.IsNullOrWhiteSpace(InviteString)
        && Invite is null;

    public event Action<bool?>? RequestClose;

    [RelayCommand]
    private void Browse()
    {
        var picked = _folderBrowser?.Invoke();
        // net48: у string.IsNullOrEmpty нет NotNullWhen-аннотаций — паттерн-матчинг.
        if (picked is { Length: > 0 })
            FolderPath = picked;
    }

    private bool CanOk => Mode == ConnectMode.LocalFolder
        ? !string.IsNullOrWhiteSpace(FolderPath)
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

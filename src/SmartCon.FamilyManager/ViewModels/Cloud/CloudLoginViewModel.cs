using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels.Cloud;

/// <summary>
/// Диалог входа/регистрации облачного аккаунта (§7.3.1 — «шаг аккаунта
/// первым в мастерах»). Modal: без аккаунта дальше нельзя. Регистрация —
/// прямо здесь, переключателем режима. Пароль живёт в памяти только на время
/// запроса (в Credential Manager уходит только refresh-токен, ADR-076 §2).
/// </summary>
public sealed partial class CloudLoginViewModel : ObservableObject, SmartCon.Core.Services.Interfaces.IObservableRequestClose
{
    /// <summary>Endpoint по умолчанию для среза v1 — dev-сервер владельца (master plan §11 C0).</summary>
    public const string DefaultEndpoint = "http://127.0.0.1:8787";

    private readonly CloudAuthService _auth;

    [ObservableProperty] private string _endpoint;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoginMode))]
    [NotifyPropertyChangedFor(nameof(ActionButtonText))]
    private bool _isRegisterMode;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorText;

    public bool IsLoginMode => !IsRegisterMode;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public string ActionButtonText => LanguageManager.GetString(IsRegisterMode
        ? StringLocalization.Keys.FM_Cloud_RegisterButton
        : StringLocalization.Keys.FM_Cloud_LoginButton) ?? (IsRegisterMode ? "Зарегистрироваться" : "Войти");

    public bool Success { get; private set; }

    public event Action<bool?>? RequestClose;

    public CloudLoginViewModel(CloudAuthService auth)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _endpoint = auth.CurrentAccount?.Endpoint ?? DefaultEndpoint;
    }

    private bool CanSubmit =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(Endpoint)
        && !string.IsNullOrWhiteSpace(Email)
        && !string.IsNullOrWhiteSpace(Password)
        && (!IsRegisterMode || !string.IsNullOrWhiteSpace(DisplayName));

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync(CancellationToken ct)
    {
        ErrorText = null;
        IsBusy = true;
        SubmitCommand.NotifyCanExecuteChanged();
        try
        {
            using var _scope = SmartConLogger.BeginScope("CloudLogin",
                ("Method", nameof(SubmitAsync)),
                ("IsRegister", IsRegisterMode),
                ("Endpoint", Endpoint));
            var ok = IsRegisterMode
                ? await _auth.RegisterAsync(Endpoint.Trim(), Email.Trim(), Password, DisplayName.Trim(), ct).ConfigureAwait(true)
                : await _auth.LoginAsync(Endpoint.Trim(), Email.Trim(), Password, ct).ConfigureAwait(true);
            if (!ok)
            {
                ErrorText = string.Format(
                    LanguageManager.GetString(IsRegisterMode
                        ? StringLocalization.Keys.FM_Cloud_RegisterFailed
                        : StringLocalization.Keys.FM_Cloud_LoginFailed) ?? "Операция не выполнена: {0}",
                    LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_LoginTitle) ?? "облачный аккаунт");
                return;
            }

            Success = true;
            SmartConLogger.Info($"Cloud login ok (register={IsRegisterMode})");
            RequestClose?.Invoke(true);
        }
        catch (OperationCanceledException)
        {
            // закрытие диалога во время запроса — не ошибка
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"Cloud login failed: {ex.Message} [Action: проверьте адрес сервера, email и пароль]");
            ErrorText = string.Format(
                LanguageManager.GetString(IsRegisterMode
                    ? StringLocalization.Keys.FM_Cloud_RegisterFailed
                    : StringLocalization.Keys.FM_Cloud_LoginFailed) ?? "Операция не выполнена: {0}",
                ex.Message);
        }
        finally
        {
            IsBusy = false;
            Password = string.Empty;
            SubmitCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(null);

    partial void OnIsRegisterModeChanged(bool value) => SubmitCommand.NotifyCanExecuteChanged();
    partial void OnEndpointChanged(string value) => SubmitCommand.NotifyCanExecuteChanged();
    partial void OnEmailChanged(string value) => SubmitCommand.NotifyCanExecuteChanged();
    partial void OnPasswordChanged(string value) => SubmitCommand.NotifyCanExecuteChanged();
    partial void OnDisplayNameChanged(string value) => SubmitCommand.NotifyCanExecuteChanged();
}

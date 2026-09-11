using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Cloud;

/// <summary>
/// Аутентификация облака (ADR-076): login/register → JWT access (30 мин, память) +
/// rotating refresh (7 дней, Windows Credential Manager). GetAccessTokenAsync освежает
/// access заранее (буфер 60 с) под SemaphoreSlim; 401 на refresh = сессия отозвана
/// (reuse-детект сервера) → аккаунт сбрасывается.
/// </summary>
public sealed class CloudAuthService : IDisposable
{
    private const int RefreshBufferSeconds = 60;

    private readonly HttpClient _http;
    private readonly ICloudCredentialStore _credentials;
    private readonly IClock _clock;
    private readonly string _accountFilePath;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private CloudAccount? _account;
    private string? _accessToken;
    private DateTimeOffset _accessExpiresAtUtc;

    /// <summary>Меняется при login/logout (UI обновляет состояние кнопок).</summary>
    public event Action? AccountChanged;

    public CloudAuthService(ICloudCredentialStore credentials, IClock clock)
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, credentials, clock,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SmartCon", "FamilyManager", "cloud-account.json"))
    {
    }

    public CloudAuthService(HttpClient http, ICloudCredentialStore credentials, IClock clock, string accountFilePath)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _accountFilePath = accountFilePath;
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        LoadAccount();
    }

    public CloudAccount? CurrentAccount => _account;

    public bool IsLoggedIn => _account is not null;

    /// <summary>POST /v1/auth/login. false = неверный email/пароль (401).</summary>
    public async Task<bool> LoginAsync(string endpoint, string email, string password, CancellationToken ct = default)
    {
        var response = await PostJsonAsync($"{NormalizeEndpoint(endpoint)}/v1/auth/login",
            new { email = email.Trim(), password }, authenticated: false, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return false;
            throw await CloudApiExceptionAsync(response, ct).ConfigureAwait(false);
        }

        await StoreSessionAsync(endpoint, response, ct).ConfigureAwait(false);
        SmartConLogger.Info($"Cloud login ok: {email} @ {endpoint}");
        return true;
    }

    /// <summary>POST /v1/auth/register. false = регистрация не выполнена (валидация/занят, без деталей — anti-enumeration).</summary>
    public async Task<bool> RegisterAsync(
        string endpoint, string email, string password, string displayName, CancellationToken ct = default)
    {
        var response = await PostJsonAsync($"{NormalizeEndpoint(endpoint)}/v1/auth/register",
            new { email = email.Trim(), password, displayName = displayName.Trim() }, authenticated: false, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) return false;
            throw await CloudApiExceptionAsync(response, ct).ConfigureAwait(false);
        }

        await StoreSessionAsync(endpoint, response, ct).ConfigureAwait(false);
        SmartConLogger.Info($"Cloud register ok: {email} @ {endpoint}");
        return true;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var account = _account;
        var refreshToken = account is null ? null : _credentials.LoadToken(account.Endpoint, account.Email);
        ClearSession();
        if (account is not null) _credentials.DeleteToken(account.Endpoint, account.Email);
        AccountChanged?.Invoke();

        if (refreshToken is not null && account is not null)
        {
            // Сервер отзывает refresh-цепочку; локальная ошибка отзыва не блокирует logout.
            try
            {
                await PostJsonAsync($"{NormalizeEndpoint(account.Endpoint)}/v1/auth/logout",
                    new { refreshToken }, authenticated: false, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or CloudApiException)
            {
                SmartConLogger.Warn(
                    $"Cloud logout call failed ({ex.GetType().Name}). Local session cleared anyway. " +
                    "[Action: не требуется — refresh-токен истечёт сам (7 дней) или отзовётся reuse-детектом]");
            }
        }
    }

    /// <summary>Валидный access-токен или null (нет сессии / refresh отклонён — нужен повторный вход).</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_account is null) return null;
        if (_accessToken is not null && _clock.UtcNow.AddSeconds(RefreshBufferSeconds) < _accessExpiresAtUtc)
            return _accessToken;

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Параллельный вызов мог освежить, пока ждали лок.
            if (_accessToken is not null && _clock.UtcNow.AddSeconds(RefreshBufferSeconds) < _accessExpiresAtUtc)
                return _accessToken;

            var refreshToken = _credentials.LoadToken(_account.Endpoint, _account.Email);
            if (refreshToken is null)
            {
                ClearSession();
                return null;
            }

            using var response = await PostJsonAsync($"{NormalizeEndpoint(_account.Endpoint)}/v1/auth/refresh",
                new { refreshToken }, authenticated: false, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Refresh отозван (rotating + reuse-детект E16) или истёк: сессия мертва —
                // токен из store удаляем, повторное использование отзовёт все сессии сервером.
                SmartConLogger.Warn(
                    "Cloud refresh rejected — session revoked or expired. " +
                    "[Action: войдите заново через диалог входа в облачный каталог]");
                _credentials.DeleteToken(_account.Endpoint, _account.Email);
                ClearSession();
                AccountChanged?.Invoke();
                return null;
            }
            if (!response.IsSuccessStatusCode)
                throw await CloudApiExceptionAsync(response, ct).ConfigureAwait(false);

            var pair = await ReadJsonAsync<TokenResponse>(response, ct).ConfigureAwait(false);
            if (pair is null || string.IsNullOrEmpty(pair.AccessToken))
                throw new CloudApiException((int)response.StatusCode, null, "пустой ответ /v1/auth/refresh");

            _accessToken = pair.AccessToken;
            _accessExpiresAtUtc = _clock.UtcNow.AddMinutes(pair.ExpiresInMinutes);
            if (!string.IsNullOrEmpty(pair.RefreshToken))
                _credentials.SaveToken(_account.Endpoint, _account.Email, pair.RefreshToken!);
            return _accessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Форсирует refresh даже при «ещё валидном» по времени access — сервер мог отозвать
    /// токен (logout с другой машины, reuse-детект). При 401 сбрасывает сессию.
    /// </summary>
    public async Task ForceRefreshAsync(CancellationToken ct = default)
    {
        if (_account is null) return;
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _accessToken = null;
        }
        finally
        {
            _refreshLock.Release();
        }
        await GetAccessTokenAsync(ct).ConfigureAwait(false);
    }

    private async Task StoreSessionAsync(string endpoint, HttpResponseMessage response, CancellationToken ct)
    {
        var auth = await ReadJsonAsync<AuthResponse>(response, ct).ConfigureAwait(false)
            ?? throw new CloudApiException((int)response.StatusCode, null, "пустой ответ auth");
        var normalized = NormalizeEndpoint(endpoint);
        _account = new CloudAccount(normalized, auth.User.Email, auth.User.DisplayName, auth.User.Id.ToString());
        _accessToken = auth.AccessToken;
        _accessExpiresAtUtc = _clock.UtcNow.AddMinutes(auth.ExpiresInMinutes);
        _credentials.SaveToken(normalized, auth.User.Email, auth.RefreshToken);
        SaveAccount();
        AccountChanged?.Invoke();
    }

    private void ClearSession()
    {
        _account = null;
        _accessToken = null;
        _accessExpiresAtUtc = default;
        try
        {
            if (File.Exists(_accountFilePath)) File.Delete(_accountFilePath);
        }
        catch (IOException)
        {
            // Недоступный файл аккаунта не должен ломать logout.
        }
    }

    private void LoadAccount()
    {
        try
        {
            if (!File.Exists(_accountFilePath)) return;
            var account = JsonSerializer.Deserialize<CloudAccount>(File.ReadAllText(_accountFilePath));
            if (account is not null && !string.IsNullOrEmpty(account.Endpoint)) _account = account;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            SmartConLogger.Warn(
                "Cloud account file unreadable — ignored. [Action: войдите заново, файл будет перезаписан]");
        }
    }

    private void SaveAccount()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_accountFilePath)!);
            File.WriteAllText(_accountFilePath, JsonSerializer.Serialize(_account));
        }
        catch (IOException ex)
        {
            SmartConLogger.Warn(
                $"Cloud account file not persisted ({ex.GetType().Name}): повторный вход потребуется после рестарта. " +
                "[Action: проверьте доступ к %APPDATA%\\SmartCon\\FamilyManager]");
        }
    }

    private async Task<HttpResponseMessage> PostJsonAsync(
        string url, object body, bool authenticated, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static async Task<CloudApiException> CloudApiExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var code = (string?)null;
        var message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        try
        {
            if (response.Content.Headers.ContentType?.MediaType == "application/json")
            {
#if NET8_0_OR_GREATER
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
#else
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("code", out var c)) code = c.GetString();
                if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                    message = e.GetString() ?? message;
            }
        }
        catch (JsonException)
        {
            // не-JSON тело — оставляем HTTP-строку
        }
        return new CloudApiException((int)response.StatusCode, code, message);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true, // сервер отдаёт camelCase (ASP.NET Core default)
    };

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
#if NET8_0_OR_GREATER
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
#else
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif
        return JsonSerializer.Deserialize<T>(json, SerializerOptions);
    }

    internal static string NormalizeEndpoint(string endpoint)
    {
        var value = endpoint.Trim().TrimEnd('/');
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = "https://" + value;
        return value;
    }

    public void Dispose() => _http.Dispose();

    private sealed record AuthResponse(string AccessToken, string RefreshToken, int ExpiresInMinutes, UserDto User);

    private sealed record UserDto(Guid Id, string DisplayName, string Email);

    private sealed record TokenResponse(string AccessToken, string? RefreshToken, int ExpiresInMinutes);
}

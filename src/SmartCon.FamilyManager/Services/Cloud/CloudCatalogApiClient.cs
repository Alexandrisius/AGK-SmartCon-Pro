using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SmartCon.FamilyManager.Models.Cloud;

namespace SmartCon.FamilyManager.Services.Cloud;

/// <summary>DTO ответа POST /v1/catalogs (срез-контракт сервера).</summary>
public sealed record CloudCatalogDto(string Id, string Slug, string Name, long CurrentPublishSeq);

/// <summary>DTO ответа GET /v1/catalogs/{slug}/manifest/latest.</summary>
public sealed record CloudManifestLatestDto(long PublishSeq, long ManifestSizeBytes, CatalogManifestV1 Manifest);

/// <summary>DTO ответа POST .../publish.</summary>
public sealed record CloudPublishResultDto(long PublishSeq);

/// <summary>
/// REST-клиент SmartCon.Cloud (срез-контракт §6.2): все авторизованные вызовы идут через
/// SendAuthorizedAsync — Bearer из CloudAuthService, один retry при 401 (access истёк между
/// получением и отправкой). GET /v1/files/{sha256} — анонимный (presigned-семантика среза).
/// </summary>
public sealed class CloudCatalogApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly CloudAuthService _auth;

    public CloudCatalogApiClient(CloudAuthService auth)
        : this(auth, new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
    {
    }

    public CloudCatalogApiClient(CloudAuthService auth, HttpClient http)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Add("User-Agent", "SmartCon-CloudClient");
    }

    public string? Endpoint => _auth.CurrentAccount?.Endpoint;

    /// <summary>Текущий аккаунт облака (display name нужен publish-флоу для PII-safe publishedBy).</summary>
    public CloudAccount? CurrentAccount => _auth.CurrentAccount;

    /// <summary>POST /v1/catalogs — создать каталог (режим б §7.3.1: пустая облачная база).</summary>
    public async Task<CloudCatalogDto> CreateCatalogAsync(string name, CancellationToken ct = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, "/v1/catalogs",
            jsonBody: JsonSerializer.Serialize(new { name }), ct: ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<CloudCatalogDto>(response, ct).ConfigureAwait(false);
        return dto ?? throw new CloudApiException((int)response.StatusCode, null, "пустой ответ /v1/catalogs");
    }

    /// <summary>GET /v1/catalogs/{slug}/manifest/latest. 404 → null (ещё нет публикаций).</summary>
    public async Task<CloudManifestLatestDto?> GetLatestManifestAsync(string slug, CancellationToken ct = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"/v1/catalogs/{slug}/manifest/latest",
            ct: ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var raw = await ReadJsonAsync<LatestResponse>(response, ct).ConfigureAwait(false)
            ?? throw new CloudApiException((int)response.StatusCode, null, "пустой ответ manifest/latest");
        var manifest = raw.Manifest.Deserialize<CatalogManifestV1>(CatalogManifestJson.Read)
            ?? throw new CloudApiException((int)response.StatusCode, null, "манифест не распарсен");
        return new CloudManifestLatestDto(raw.PublishSeq, raw.ManifestSizeBytes, manifest);
    }

    /// <summary>POST /v1/catalogs/{slug}/publish — новый publish point (манифест целиком, срез-модель).</summary>
    public async Task<long> PublishAsync(string slug, CatalogManifestV1 manifest, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { manifest }, CatalogManifestJson.WriteCompact);
        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"/v1/catalogs/{slug}/publish",
            jsonBody: body, ct: ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<CloudPublishResultDto>(response, ct).ConfigureAwait(false);
        return dto?.PublishSeq ?? throw new CloudApiException((int)response.StatusCode, null, "пустой ответ publish");
    }

    /// <summary>POST /v1/catalogs/{slug}/subscribe — идемпотентная подписка (restore E30).</summary>
    public async Task SubscribeAsync(string slug, CancellationToken ct = default)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"/v1/catalogs/{slug}/subscribe",
            jsonBody: "{}", ct: ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>PUT /v1/files/{sha256} — потоковая загрузка CAS-объекта (upload до publish).</summary>
    public async Task UploadFileAsync(string sha256, Stream content, CancellationToken ct = default)
    {
        var endpoint = _auth.CurrentAccount?.Endpoint
            ?? throw new CloudApiException(401, "no_session", "нет активной облачной сессии — войдите в систему");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{endpoint}/v1/files/{sha256}")
        {
            Content = new StreamContent(content),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        AttachBearer(request, await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>GET /v1/files/{sha256} — анонимная загрузка (presigned-семантика: URL = токен).</summary>
    public async Task<Stream> DownloadFileAsync(string sha256, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"{_auth.CurrentAccount!.Endpoint}/v1/files/{sha256}",
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
#if NET8_0_OR_GREATER
        return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#else
        return await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method, string relativePath, string? jsonBody = null, CancellationToken ct = default)
    {
        var endpoint = _auth.CurrentAccount?.Endpoint
            ?? throw new CloudApiException(401, "no_session", "нет активной облачной сессии — войдите в систему");

        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(method, endpoint + relativePath);
            if (jsonBody is not null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }
            AttachBearer(request, await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false));
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt == 1) break;

            // 401 при валидном по времени access — почти наверняка отозванный/невалидный токен:
            // форсируем refresh (SemaphoreSlim внутри) и ретраим ровно один раз.
            response.Dispose();
            await ForceRefreshAsync(ct).ConfigureAwait(false);
        }
        return response!;
    }

    private async Task ForceRefreshAsync(CancellationToken ct)
    {
        // GetAccessTokenAsync уже осведомлён о буфере; прямой вызов RefreshForcedAsync ниже
        // гарантирует refresh даже при «ещё валидном» access (сервер его отозвал).
        await _auth.ForceRefreshAsync(ct).ConfigureAwait(false);
    }

    private static void AttachBearer(HttpRequestMessage request, string? token)
    {
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var code = (string?)null;
        var message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        IReadOnlyList<string>? missing = null;
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
                if (string.Equals(code, "missing_objects", StringComparison.Ordinal)
                    && doc.RootElement.TryGetProperty("missing", out var m)
                    && m.ValueKind == JsonValueKind.Array)
                {
                    missing = m.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!)
                        .ToList();
                }
            }
        }
        catch (JsonException)
        {
            // не-JSON тело — стандартная HTTP-строка
        }
        throw new CloudApiException((int)response.StatusCode, code, message, missing);
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

    public void Dispose() => _http.Dispose();

    private sealed record LatestResponse(long PublishSeq, long ManifestSizeBytes, JsonElement Manifest);
}

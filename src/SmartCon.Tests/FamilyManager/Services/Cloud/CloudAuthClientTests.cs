using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Models.Cloud;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services.Cloud;

/// <summary>
/// CloudAuthService + CloudCatalogApiClient (срез v1): login/refresh-ротация/401-сброс
/// сессии, retry-once на 401, парсинг серверных code/error (stale_base_seq и т.д.).
/// HTTP — через fake-хендлер (стек ответов), credential store — in-memory.
/// </summary>
public sealed class CloudAuthClientTests : IDisposable
{
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryCredentialStore _credentials = new();
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly CloudAuthService _auth;
    private readonly string _accountFile;

    public CloudAuthClientTests()
    {
        _accountFile = Path.Combine(Path.GetTempPath(), $"cloud-account-{Guid.NewGuid():N}.json");
        _auth = new CloudAuthService(new HttpClient(_handler), _credentials, _clock, _accountFile);
    }

    public void Dispose()
    {
        _auth.Dispose();
        if (File.Exists(_accountFile)) File.Delete(_accountFile);
    }

    private static HttpContent Json(object body) =>
        new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static readonly string AuthBody = JsonSerializer.Serialize(new
    {
        accessToken = "access-1",
        refreshToken = "refresh-1",
        expiresInMinutes = 30,
        user = new { id = Guid.Parse("f47ac10b-58cc-4372-a567-0e02b2c3d479"), displayName = "Иван", email = "ivan@proekt.ru" },
    });

    [Fact]
    public async Task LoginAsync_ValidCredentials_StoresSessionAndToken()
    {
        _handler.Enqueue(FakeHttp.JsonOk(AuthBody));
        _handler.Enqueue(FakeHttp.JsonOk(
            JsonSerializer.Serialize(new { accessToken = "access-2", refreshToken = "refresh-2", expiresInMinutes = 30 })));

        var ok = await _auth.LoginAsync("http://127.0.0.1:8787", "ivan@proekt.ru", "pw");
        var token = await _auth.GetAccessTokenAsync();

        Assert.True(ok);
        Assert.True(_auth.IsLoggedIn);
        Assert.Equal("Иван", _auth.CurrentAccount!.DisplayName);
        Assert.Equal("refresh-1", _credentials.LoadToken("http://127.0.0.1:8787", "ivan@proekt.ru"));
        // access ещё жив — refresh не вызывается, отдаётся кэшированный.
        Assert.Equal("access-1", token);
        Assert.Equal("/v1/auth/login", _handler.Requests[0].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_ReturnsFalse()
    {
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var ok = await _auth.LoginAsync("http://127.0.0.1:8787", "ivan@proekt.ru", "wrong");

        Assert.False(ok);
        Assert.False(_auth.IsLoggedIn);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ExpiredAccess_RefreshesAndRotates()
    {
        _handler.Enqueue(FakeHttp.JsonOk(AuthBody));
        await _auth.LoginAsync("http://127.0.0.1:8787", "ivan@proekt.ru", "pw");
        // access истёк по часам.
        _clock.UtcNow = _clock.UtcNow.AddMinutes(31);
        _handler.Enqueue(FakeHttp.JsonOk(
            JsonSerializer.Serialize(new { accessToken = "access-2", refreshToken = "refresh-2", expiresInMinutes = 30 })));

        var token = await _auth.GetAccessTokenAsync();

        Assert.Equal("access-2", token);
        Assert.Equal("/v1/auth/refresh", _handler.Requests[^1].RequestUri!.PathAndQuery);
        // ротация: в store лежит НОВЫЙ refresh.
        Assert.Equal("refresh-2", _credentials.LoadToken("http://127.0.0.1:8787", "ivan@proekt.ru"));
    }

    [Fact]
    public async Task GetAccessTokenAsync_RefreshRejected_ClearsSession()
    {
        _handler.Enqueue(FakeHttp.JsonOk(AuthBody));
        await _auth.LoginAsync("http://127.0.0.1:8787", "ivan@proekt.ru", "pw");
        _clock.UtcNow = _clock.UtcNow.AddMinutes(31);
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var token = await _auth.GetAccessTokenAsync();

        Assert.Null(token);
        Assert.False(_auth.IsLoggedIn);
        Assert.Null(_credentials.LoadToken("http://127.0.0.1:8787", "ivan@proekt.ru"));
    }

    [Fact]
    public async Task ApiClient_409StaleBaseSeq_ThrowsWithServerCode()
    {
        _handler.Enqueue(FakeHttp.JsonOk(AuthBody));
        await _auth.LoginAsync("http://127.0.0.1:8787", "ivan@proekt.ru", "pw");
        using var client = new CloudCatalogApiClient(_auth, new HttpClient(_handler));
        _handler.Enqueue(FakeHttp.JsonError(409, "stale_base_seq", "каталог уже опубликован"));

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.PublishAsync("slug", new CatalogManifestV1 { CatalogId = "id", PublishSeq = 1 }));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("stale_base_seq", ex.Code);
        Assert.Contains("опубликован", ex.Message);
    }

    [Fact]
    public async Task ApiClient_UnauthorizedThenRefresh_RetriesOnce()
    {
        _handler.Enqueue(FakeHttp.JsonOk(AuthBody));
        await _auth.LoginAsync("http://127.0.0.1:8787", "ivan@proekt.ru", "pw");
        using var client = new CloudCatalogApiClient(_auth, new HttpClient(_handler));

        // 1) API зовёт с access-1 → 401 (сервер отозвал); 2) forced refresh → access-2;
        // 3) retry тем же запросом → 200.
        _handler.Enqueue(FakeHttp.JsonError(401, "invalid_token", "токен отозван"));
        _handler.Enqueue(FakeHttp.JsonOk(
            JsonSerializer.Serialize(new { accessToken = "access-2", refreshToken = "refresh-2", expiresInMinutes = 30 })));
        _handler.Enqueue(FakeHttp.JsonOk(
            JsonSerializer.Serialize(new { id = Guid.NewGuid(), slug = "slug", name = "Каталог", currentPublishSeq = 0 })));

        var catalog = await client.CreateCatalogAsync("Каталог");

        Assert.Equal("slug", catalog.Slug);
        Assert.Equal(2, _handler.Requests.Count(r => r.RequestUri!.PathAndQuery == "/v1/catalogs"));
        var retryAuth = _handler.Requests
            .Where(r => r.RequestUri!.PathAndQuery == "/v1/catalogs")
            .Select(r => r.Headers.Authorization?.Parameter)
            .ToList();
        Assert.Equal("access-1", retryAuth[0]);
        Assert.Equal("access-2", retryAuth[1]);
    }

    [Fact]
    public async Task ApiClient_GetLatestManifest_ParsesManifest()
    {
        _handler.Enqueue(FakeHttp.JsonOk(AuthBody));
        await _auth.LoginAsync("http://127.0.0.1:8787", "ivan@proekt.ru", "pw");
        using var client = new CloudCatalogApiClient(_auth, new HttpClient(_handler));

        var manifestDto = new CatalogManifestV1
        {
            CatalogId = "c1",
            PublishSeq = 5,
            PublishedBy = "Автор",
            Items = [new ManifestItemV1 { Id = "i1", Name = "Отвод" }],
        };
        var payload = JsonSerializer.Serialize(new
        {
            publishSeq = 5,
            manifestSizeBytes = 1234,
            manifest = manifestDto,
        }, CatalogManifestJson.WriteCompact);
        _handler.Enqueue(FakeHttp.JsonOk(payload));

        var latest = await client.GetLatestManifestAsync("slug");

        Assert.NotNull(latest);
        Assert.Equal(5, latest!.PublishSeq);
        Assert.Equal("Отвод", Assert.Single(latest.Manifest.Items).Name);
    }

    private sealed class InMemoryCredentialStore : ICloudCredentialStore
    {
        private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);

        public void SaveToken(string endpoint, string email, string refreshToken) =>
            _tokens[$"{endpoint}|{email}"] = refreshToken;

        public string? LoadToken(string endpoint, string email) =>
            _tokens.TryGetValue($"{endpoint}|{email}", out var token) ? token : null;

        public void DeleteToken(string endpoint, string email) => _tokens.Remove($"{endpoint}|{email}");
    }
}

internal static class FakeHttp
{
    internal static HttpResponseMessage JsonOk(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    internal static HttpResponseMessage JsonError(int status, string code, string error) => new((HttpStatusCode)status)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { code, error }), Encoding.UTF8, "application/json"),
    };
}

/// <summary>Стек ответов + лог запросов (auth-заголовки доступны для ассертов).</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly List<string?> _bodies = [];

    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    /// <summary>Тела запросов по индексу Requests (content живёт дольше request-объекта).</summary>
    public IReadOnlyList<string?> Bodies => _bodies;

    public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        _requests.Add(request);
        _bodies.Add(body);
        if (_responses.Count == 0)
            throw new InvalidOperationException("FakeHttpMessageHandler: очередь ответов пуста");
        return _responses.Dequeue();
    }
}

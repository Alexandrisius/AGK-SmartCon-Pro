using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SmartCon.FamilyManager.Models.Cloud;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services.Cloud;

internal sealed class InMemoryCredentialStore : ICloudCredentialStore
{
    private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public void SaveToken(string endpoint, string email, string refreshToken) => _tokens[$"{endpoint}|{email}"] = refreshToken;
    public string? LoadToken(string endpoint, string email) => _tokens.TryGetValue($"{endpoint}|{email}", out var t) ? t : null;
    public void DeleteToken(string endpoint, string email) => _tokens.Remove($"{endpoint}|{email}");
}

/// <summary>
/// sync-state.json — единый источник локального seq подписной копии.
/// Регрессия ретеста 2026-09-11: VM парсила файл ключом "publishSeq" (camelCase),
/// а сервис пишет "PublishSeq" (PascalCase) → локальный seq вечно null → синяя
/// точка «доступны обновления» не гасла никогда, при этом pull говорил «актуально».
/// </summary>
public sealed class CloudSyncStateTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"CloudSyncState_{Guid.NewGuid():N}");
    private readonly CloudSyncService _sync;

    public CloudSyncStateTests()
    {
        Directory.CreateDirectory(_workDir);
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var handler = new FakeHttpMessageHandler();
        var auth = new CloudAuthService(new HttpClient(handler), new InMemoryCredentialStore(), clock,
            Path.Combine(_workDir, "account.json"));
        _sync = new CloudSyncService(
            new CloudCatalogApiClient(auth, new HttpClient(handler)),
            new CatalogManifestApplier(clock),
            clock,
            Path.Combine(_workDir, "cache"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ReadLocalPublishSeq_PascalCaseKey_ReturnsSeq()
    {
        // Ровно то, что пишет WriteSyncState: JsonSerializer.Serialize(SyncState) без
        // naming policy → "PublishSeq". Старый парсер VM искал "publishSeq" → null.
        var root = Path.Combine(_workDir, "copy");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "sync-state.json"),
            "{\"PublishSeq\":5,\"SyncedAtUtc\":\"2026-09-11T12:00:00+00:00\"}");

        Assert.Equal(5, _sync.ReadLocalPublishSeq(root));
    }

    [Fact]
    public void ReadLocalPublishSeq_NoStateFile_ReturnsNull()
    {
        var root = Path.Combine(_workDir, "fresh-copy");
        Directory.CreateDirectory(root);

        Assert.Null(_sync.ReadLocalPublishSeq(root));
    }

    [Fact]
    public void ReadLocalPublishSeq_CorruptJson_ReturnsNull()
    {
        var root = Path.Combine(_workDir, "broken-copy");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "sync-state.json"), "{not json");

        Assert.Null(_sync.ReadLocalPublishSeq(root));
    }
}

/// <summary>
/// Unpublish-клиент: DELETE /v1/catalogs/{slug} и парсинг problem+json ошибок
/// (code/detail из extensions — 410 catalog_unpublished, 403 not_owner).
/// </summary>
public sealed class CloudUnpublishClientTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"CloudUnpublish_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }

    private (CloudCatalogApiClient Api, CloudAuthService Auth, FakeHttpMessageHandler Handler) MakeClient(string accountFile)
    {
        var handler = new FakeHttpMessageHandler();
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var auth = new CloudAuthService(new HttpClient(handler), new InMemoryCredentialStore(), clock,
            Path.Combine(_workDir, accountFile));
        return (new CloudCatalogApiClient(auth, new HttpClient(handler)), auth, handler);
    }

    private static void EnqueueLogin(FakeHttpMessageHandler handler) =>
        handler.Enqueue(FakeHttp.JsonOk(JsonSerializer.Serialize(new
        {
            accessToken = "a1",
            refreshToken = "r1",
            expiresInMinutes = 30,
            user = new { id = Guid.NewGuid(), displayName = "Иван", email = "i@p.ru" },
        })));

    private static HttpResponseMessage Problem(HttpStatusCode status, string code, string title, string detail) => new(status)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { status = (int)status, title, detail, code }),
            Encoding.UTF8, "application/problem+json"),
    };

    [Fact]
    public async Task UnpublishCatalogAsync_Ok_DoesNotThrow()
    {
        var (api, auth, handler) = MakeClient("ok.json");
        EnqueueLogin(handler);
        Assert.True(await auth.LoginAsync("http://cloud", "i@p.ru", "pw"));

        handler.Enqueue(FakeHttp.JsonOk("{}"));
        await api.UnpublishCatalogAsync("demo");
    }

    [Fact]
    public async Task UnpublishCatalogAsync_NotOwnerProblemJson_ParsesCodeAndDetail()
    {
        var (api, auth, handler) = MakeClient("forbidden.json");
        EnqueueLogin(handler);
        Assert.True(await auth.LoginAsync("http://cloud", "i@p.ru", "pw"));

        handler.Enqueue(Problem(HttpStatusCode.Forbidden, "not_owner",
            "снять с публикации может только владелец каталога", "только Owner"));
        var ex = await Assert.ThrowsAsync<CloudApiException>(() => api.UnpublishCatalogAsync("demo"));

        Assert.Equal(403, ex.StatusCode);
        Assert.Equal("not_owner", ex.Code);
        Assert.Contains("Owner", ex.Message);
    }

    [Fact]
    public async Task GetLatestManifestAsync_Gone410_ThrowsCatalogUnpublished()
    {
        var (api, auth, handler) = MakeClient("gone.json");
        EnqueueLogin(handler);
        Assert.True(await auth.LoginAsync("http://cloud", "i@p.ru", "pw"));

        handler.Enqueue(Problem(HttpStatusCode.Gone, "catalog_unpublished",
            "каталог снят с публикации", "каталог 'demo' снят с публикации автором"));
        var ex = await Assert.ThrowsAsync<CloudApiException>(() => api.GetLatestManifestAsync("demo"));

        Assert.Equal(410, ex.StatusCode);
        Assert.Equal("catalog_unpublished", ex.Code);
    }
}

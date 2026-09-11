using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SmartCon.FamilyManager.Models.Cloud;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.Tests.FamilyManager.Repository;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services.Cloud;

/// <summary>
/// Фазы publish/sync поверх fake-сервера (FakeHttpMessageHandler): полный цикл
/// «pull-before-push → 422 missing → upload → publish» и «latest → download в кэш →
/// apply → swap → sync-state», идемпотентность seq (E1/E2).
/// </summary>
public sealed class CloudPublishSyncFlowTests : IDisposable
{
    private readonly TempCatalogFixture _source = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly CloudAuthService _auth;
    private readonly CloudCatalogApiClient _api;
    private readonly CloudPublishService _publish;
    private readonly CloudSyncService _sync;
    private readonly string _workDir;

    public CloudPublishSyncFlowTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"CloudFlow_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _auth = new CloudAuthService(new HttpClient(_handler), new InMemoryStore(), _clock,
            Path.Combine(_workDir, "account.json"));
        _api = new CloudCatalogApiClient(_auth, new HttpClient(_handler));
        _publish = new CloudPublishService(
            _api,
            new CatalogManifestBuilder(_source.GetDatabase(), new StoragePathResolver(_source.GetDatabase()), _clock),
            _clock);
        _sync = new CloudSyncService(_api, new CatalogManifestApplier(_clock), _clock,
            Path.Combine(_workDir, "cache"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
        _source.Dispose();
    }

    private async Task LoginAsync()
    {
        _handler.Enqueue(FakeHttp.JsonOk(JsonSerializer.Serialize(new
        {
            accessToken = "a1",
            refreshToken = "r1",
            expiresInMinutes = 30,
            user = new { id = Guid.NewGuid(), displayName = "Иван", email = "i@p.ru" },
        })));
        Assert.True(await _auth.LoginAsync("http://cloud", "i@p.ru", "pw"));
    }

    [Fact]
    public async Task PublishAsync_MissingObjects_UploadsThenPublishes()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_source, "Отвод", "v1");
        await LoginAsync();

        // 1) latest → 404 (ещё нет публикаций) → build с publishSeq=1.
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        // 2) publish → 422 missing_objects [sha]
        var sha = await ReadFileShaAsync();
        _handler.Enqueue(new HttpResponseMessage((HttpStatusCode)422)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { code = "missing_objects", error = "сначала загрузите файлы", missing = new[] { sha } }),
                Encoding.UTF8, "application/json"),
        });
        // 3) upload файла → 200
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        // 4) publish → 201 {publishSeq:1}
        _handler.Enqueue(FakeHttp.JsonOk(JsonSerializer.Serialize(new { publishSeq = 1 })));

        var result = await _publish.PublishAsync(new CloudPublishRequest("otvody", "catalog-1"));

        Assert.Equal(1, result.PublishSeq);
        Assert.Equal(1, result.ItemsCount);
        Assert.Equal(1, result.UploadedFiles);
        // upload ушёл PUT-ом с правильным sha.
        var upload = _handler.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal($"/v1/files/{sha}", upload.RequestUri!.PathAndQuery);
        // в манифесте publish — publishSeq=1 и displayName автора.
        var publishIndex = Enumerable.Range(0, _handler.Requests.Count)
            .Last(i => _handler.Requests[i].RequestUri!.PathAndQuery == "/v1/catalogs/otvody/publish");
        var publishBody = JsonDocument.Parse(_handler.Bodies[publishIndex]!).RootElement;
        Assert.Equal(1, publishBody.GetProperty("manifest").GetProperty("publishSeq").GetInt64());
        Assert.Equal("Иван", publishBody.GetProperty("manifest").GetProperty("publishedBy").GetString());
    }

    [Fact]
    public async Task SyncAsync_NewManifest_DownloadsAppliesAndSwaps()
    {
        await LoginAsync();
        var content = Encoding.UTF8.GetBytes("FAMILY-RFA-CONTENT");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        var itemId = Guid.NewGuid().ToString("N");
        var manifest = new CatalogManifestV1
        {
            CatalogId = "catalog-1",
            PublishSeq = 3,
            PublishedBy = "Автор",
            Items =
            [
                new ManifestItemV1
                {
                    Id = itemId,
                    Name = "Отвод",
                    NormalizedName = "отвод",
                    CurrentVersionLabel = "v1",
                    Versions =
                    [
                        new ManifestVersionV1
                        {
                            VersionLabel = "v1",
                            SourceRevitVersion = 2025,
                            File = new ManifestFileRefV1
                            {
                                Sha256 = sha,
                                SizeBytes = content.Length,
                                FileName = "Отвод.rfa",
                            },
                        },
                    ],
                },
            ],
        };
        var latestJson = JsonSerializer.Serialize(new
        {
            publishSeq = 3,
            manifestSizeBytes = 100,
            manifest,
        }, CatalogManifestJson.WriteCompact);

        var targetRoot = Path.Combine(_workDir, "cloud", "otvody");

        // Первый sync: latest + файл.
        _handler.Enqueue(FakeHttp.JsonOk(latestJson));
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        });
        var result = await _sync.SyncAsync(new CloudSyncRequest("otvody", targetRoot, "Отводы компании"));

        Assert.True(result.Updated);
        Assert.Equal(3, result.PublishSeq);
        Assert.True(File.Exists(Path.Combine(targetRoot, "catalog.db")));
        Assert.True(File.Exists(Path.Combine(targetRoot, "files", itemId, "v1", "Отвод.rfa")));
        // кэш переживает sync
        Assert.True(File.Exists(Path.Combine(_workDir, "cache", sha[..2], sha)));

        // Второй sync с тем же seq — Updated=false, ни одного нового запроса файла.
        var requestCountBefore = _handler.Requests.Count;
        _handler.Enqueue(FakeHttp.JsonOk(latestJson));
        var again = await _sync.SyncAsync(new CloudSyncRequest("otvody", targetRoot, "Отводы компании"));
        Assert.False(again.Updated);
        for (var i = requestCountBefore; i < _handler.Requests.Count; i++)
        {
            Assert.False(_handler.Requests[i].Method == HttpMethod.Get
                && _handler.Requests[i].RequestUri!.PathAndQuery.StartsWith("/v1/files/"),
                "повторный sync при том же seq не должен качать файлы");
        }
    }

    [Fact]
    public async Task SyncAsync_NewPublishSeq_SwapsOldCopy()
    {
        await LoginAsync();
        var content = Encoding.UTF8.GetBytes("RFA-V2");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        var itemId = Guid.NewGuid().ToString("N");
        ManifestVersionV1 Version(long seq, string hash) => new()
        {
            VersionLabel = $"v{seq}",
            SourceRevitVersion = 2025,
            File = new ManifestFileRefV1 { Sha256 = hash, SizeBytes = content.Length, FileName = "Отвод.rfa" },
        };

        var targetRoot = Path.Combine(_workDir, "cloud", "otvody");

        // sync #1 (v1, старый контент)
        var oldContent = Encoding.UTF8.GetBytes("RFA-V1");
        var oldSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(oldContent)).ToLowerInvariant();
        _handler.Enqueue(FakeHttp.JsonOk(LatestJson(1, itemId, Version(1, oldSha))));
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(oldContent) });
        await _sync.SyncAsync(new CloudSyncRequest("otvody", targetRoot, "X"));

        // sync #2 (v2, новый контент + новый seq): старая копия заменяется swap-ом.
        _handler.Enqueue(FakeHttp.JsonOk(LatestJson(2, itemId, Version(2, sha))));
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        var result = await _sync.SyncAsync(new CloudSyncRequest("otvody", targetRoot, "X"));

        Assert.True(result.Updated);
        Assert.Equal(2, result.PublishSeq);
        Assert.True(File.Exists(Path.Combine(targetRoot, "files", itemId, "v2", "Отвод.rfa")));
        Assert.False(File.Exists(Path.Combine(targetRoot, "files", itemId, "v1", "Отвод.rfa")));
        // staging/backup-папок не осталось (полные имена для диагностики при падении)
        var leftovers = Directory.GetDirectories(Path.Combine(_workDir, "cloud"))
            .Where(d => Path.GetFileName(d).StartsWith("otvody.staging-", StringComparison.Ordinal)
                     || Path.GetFileName(d).StartsWith("otvody.old-", StringComparison.Ordinal))
            .ToList();
        Assert.True(leftovers.Count == 0, $"leftover swap dirs: {string.Join(", ", leftovers)}");
    }

    private static string LatestJson(long seq, string itemId, ManifestVersionV1 version)
    {
        var manifest = new CatalogManifestV1
        {
            CatalogId = "catalog-1",
            PublishSeq = seq,
            PublishedBy = "Автор",
            Items = [new ManifestItemV1
            {
                Id = itemId,
                Name = "Отвод",
                NormalizedName = "отвод",
                CurrentVersionLabel = version.VersionLabel,
                Versions = [version],
            }],
        };
        return JsonSerializer.Serialize(new { publishSeq = seq, manifestSizeBytes = 100, manifest },
            CatalogManifestJson.WriteCompact);
    }

    private static string LatestJson(long seq, params ManifestItemV1[] items)
    {
        var manifest = new CatalogManifestV1
        {
            CatalogId = "catalog-1",
            PublishSeq = seq,
            PublishedBy = "Автор",
            Items = [.. items],
        };
        return JsonSerializer.Serialize(new { publishSeq = seq, manifestSizeBytes = 100, manifest },
            CatalogManifestJson.WriteCompact);
    }

    private static ManifestItemV1 Item(string id, string name, string sha, int sizeBytes, string label = "v1", string? contentHash = null) => new()
    {
        Id = id,
        Name = name,
        NormalizedName = name.ToLowerInvariant(),
        CurrentVersionLabel = label,
        Versions =
        [
            new ManifestVersionV1
            {
                VersionLabel = label,
                // Контентная идентичность — FHV contentHash из БД; битовый file-sha
                // в детекции изменений не участвует (Revit пересохраняет .rfa рандомно).
                ContentHash = contentHash ?? "FHV-" + name + "-" + label,
                SourceRevitVersion = 2025,
                File = new ManifestFileRefV1 { Sha256 = sha, SizeBytes = sizeBytes, FileName = name + ".rfa" },
            },
        ],
    };

    [Fact]
    public async Task SyncAsync_OwnerDeletesItem_ReportsRemovalNotFullCount()
    {
        // Стресс-тест 2026-09-11: подписчик видел «добавлено 8» при удалении одного
        // семейства — дельта обязана быть per-item, а не полным размером манифеста.
        await LoginAsync();
        var contentA = Encoding.UTF8.GetBytes("DELTA-A");
        var shaA = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contentA)).ToLowerInvariant();
        var contentB = Encoding.UTF8.GetBytes("DELTA-B");
        var shaB = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contentB)).ToLowerInvariant();
        var idA = Guid.NewGuid().ToString("N");
        var idB = Guid.NewGuid().ToString("N");

        var targetRoot = Path.Combine(_workDir, "cloud", "otvody");

        // sync #1: два семейства.
        _handler.Enqueue(FakeHttp.JsonOk(LatestJson(1,
            Item(idA, "Отвод", shaA, contentA.Length), Item(idB, "Тройник", shaB, contentB.Length))));
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(contentA) });
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(contentB) });
        var first = await _sync.SyncAsync(new CloudSyncRequest("otvody", targetRoot, "X"));

        Assert.True(first.Updated);
        Assert.Equal(2, first.AddedCount);
        Assert.Equal(0, first.RemovedCount);

        // sync #2 (владелец удалил Тройник и опубликовал): кэш уже имеет оба
        // файла — докачки нет, дельта = удалено 1.
        var requestCountBefore = _handler.Requests.Count;
        _handler.Enqueue(FakeHttp.JsonOk(LatestJson(2, Item(idA, "Отвод", shaA, contentA.Length))));
        var second = await _sync.SyncAsync(new CloudSyncRequest("otvody", targetRoot, "X"));

        Assert.True(second.Updated);
        Assert.Equal(0, second.AddedCount);
        Assert.Equal(0, second.UpdatedCount);
        Assert.Equal(1, second.RemovedCount);
        Assert.Equal(1, second.ItemsCount);
        // Файл удалённого семейства исчез из копии, оставшийся — на месте.
        Assert.False(File.Exists(Path.Combine(targetRoot, "files", idB, "v1", "Тройник.rfa")));
        Assert.True(File.Exists(Path.Combine(targetRoot, "files", idA, "v1", "Отвод.rfa")));
        // Объект удалённого семейства остался в общем CAS-кэше (E1/E20: кэш
        // переживает sync, чистки в срезе нет) — файл НЕ перекачивается.
        Assert.True(File.Exists(Path.Combine(_workDir, "cache", shaB[..2], shaB)));
        for (var i = requestCountBefore; i < _handler.Requests.Count; i++)
        {
            Assert.False(_handler.Requests[i].Method == HttpMethod.Get
                && _handler.Requests[i].RequestUri!.PathAndQuery.StartsWith("/v1/files/"),
                "повторный sync не должен перекачивать закэшированные файлы");
        }
    }

    [Fact]
    public async Task SyncAsync_ItemContentChanged_ReportsUpdatedDelta()
    {
        await LoginAsync();
        var v1 = Encoding.UTF8.GetBytes("CONTENT-V1");
        var v1Sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(v1)).ToLowerInvariant();
        var v2 = Encoding.UTF8.GetBytes("CONTENT-V2-CHANGED");
        var v2Sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(v2)).ToLowerInvariant();
        var id = Guid.NewGuid().ToString("N");

        var targetRoot = Path.Combine(_workDir, "cloud", "otvody");
        _handler.Enqueue(FakeHttp.JsonOk(LatestJson(1, Item(id, "Отвод", v1Sha, v1.Length, contentHash: "FHV-1"))));
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(v1) });
        await _sync.SyncAsync(new CloudSyncRequest("otvody", targetRoot, "X"));

        // Изменился КОНТЕНТ (FHV) и файл: добавлено 0, обновлено 1.
        _handler.Enqueue(FakeHttp.JsonOk(LatestJson(2, Item(id, "Отвод", v2Sha, v2.Length, label: "v2", contentHash: "FHV-2"))));
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(v2) });
        var second = await _sync.SyncAsync(new CloudSyncRequest("otvody", targetRoot, "X"));

        Assert.True(second.Updated);
        Assert.Equal(0, second.AddedCount);
        Assert.Equal(1, second.UpdatedCount);
        Assert.Equal(0, second.RemovedCount);
    }

    [Fact]
    public async Task SyncAsync_ByteDriftOnly_NotReportedAsUpdate()
    {
        // Владелец 2026-09-11: Revit пересохраняет .rfa без изменения содержимого —
        // битовый sha меняется, FHV contentHash нет. Контент-дайджесты равны →
        // «обновлено 0» (файл перекачается молча, копия консистентна).
        await LoginAsync();
        var bytes1 = Encoding.UTF8.GetBytes("BYTES-ONE");
        var sha1 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes1)).ToLowerInvariant();
        var bytes2 = Encoding.UTF8.GetBytes("BYTES-TWO-SAME-CONTENT");
        var sha2 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes2)).ToLowerInvariant();
        var id = Guid.NewGuid().ToString("N");

        var targetRoot = Path.Combine(_workDir, "cloud", "otvody");
        _handler.Enqueue(FakeHttp.JsonOk(LatestJson(1, Item(id, "Отвод", sha1, bytes1.Length, contentHash: "FHV-SAME"))));
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes1) });
        await _sync.SyncAsync(new CloudSyncRequest("otvody", targetRoot, "X"));

        _handler.Enqueue(FakeHttp.JsonOk(LatestJson(2, Item(id, "Отвод", sha2, bytes2.Length, contentHash: "FHV-SAME"))));
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes2) });
        var second = await _sync.SyncAsync(new CloudSyncRequest("otvody", targetRoot, "X"));

        Assert.True(second.Updated); // seq вырос — копия пересобрана с новым блобом
        Assert.Equal(0, second.AddedCount);
        Assert.Equal(0, second.UpdatedCount);
        Assert.Equal(0, second.RemovedCount);
    }

    private async Task<string> ReadFileShaAsync()
    {
        var file = Directory.GetFiles(_source.GetDatabaseRoot(), "*.rfa", SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(file);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class InMemoryStore : ICloudCredentialStore
    {
        private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);

        public void SaveToken(string endpoint, string email, string refreshToken) => _tokens[$"{endpoint}|{email}"] = refreshToken;
        public string? LoadToken(string endpoint, string email) => _tokens.TryGetValue($"{endpoint}|{email}", out var t) ? t : null;
        public void DeleteToken(string endpoint, string email) => _tokens.Remove($"{endpoint}|{email}");
    }
}

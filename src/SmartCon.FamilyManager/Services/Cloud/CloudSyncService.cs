using System.IO;
using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Models.Cloud;

namespace SmartCon.FamilyManager.Services.Cloud;

public sealed record CloudSyncRequest(string Slug, string TargetRoot, string DatabaseName);

public sealed record CloudSyncResult(long PublishSeq, int ItemsCount, bool Updated, string DatabaseRoot);

/// <summary>
/// «Обновить» = только pull (решение владельца 2026-09-11): manifest/latest → докачка
/// недостающих CAS-объектов в кэш (E1: существующие пропускаются) → apply в чистую
/// временную папку → атомарный swap целевой cloud-копии. Идемпотентен: seq не изменился —
/// копия не пересобирается.
/// </summary>
public sealed class CloudSyncService
{
    private readonly CloudCatalogApiClient _api;
    private readonly CatalogManifestApplier _applier;
    private readonly IClock _clock;
    private readonly string _cacheDirectory;

    public CloudSyncService(CloudCatalogApiClient api, CatalogManifestApplier applier, IClock clock)
        : this(api, applier, clock, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SmartCon", "FamilyManager", "cloud-cache", "objects"))
    {
    }

    internal CloudSyncService(CloudCatalogApiClient api, CatalogManifestApplier applier, IClock clock, string cacheDirectory)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cacheDirectory = cacheDirectory;
    }

    /// <summary>Текущий seq сервера (бейдж «доступны обновления»). null = нет публикаций.</summary>
    public async Task<long?> GetRemotePublishSeqAsync(string slug, CancellationToken ct = default)
    {
        var latest = await _api.GetLatestManifestAsync(slug, ct).ConfigureAwait(false);
        return latest?.PublishSeq;
    }

    public async Task<CloudSyncResult> SyncAsync(
        CloudSyncRequest request,
        IProgress<CloudOperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (_api.CurrentAccount is null)
            throw new CloudApiException(401, "no_session", "нет активной облачной сессии — войдите в систему");

        using var _scope = SmartConLogger.BeginScope("CloudSync",
            ("Method", nameof(SyncAsync)),
            ("Slug", request.Slug),
            ("Target", Path.GetFileName(request.TargetRoot)));

        var latest = await _api.GetLatestManifestAsync(request.Slug, ct).ConfigureAwait(false)
            ?? throw new CloudApiException(404, "no_publications",
                $"у каталога '{request.Slug}' ещё нет публикаций. [Action: попросите владельца опубликовать изменения]");

        var state = ReadSyncState(request.TargetRoot);
        if (state is not null && state.PublishSeq == latest.PublishSeq)
        {
            SmartConLogger.Info($"Already up to date: seq={latest.PublishSeq}");
            return new CloudSyncResult(latest.PublishSeq, latest.Manifest.Items.Count, Updated: false, request.TargetRoot);
        }

        // E7-гейт: hash-эпоха манифеста опережает плагин — refuse с баннером обновления.
        if (latest.Manifest.HashFormatVersion is { } fhv && fhv > FamilyContentHashFormat.CurrentVersion)
        {
            throw new CloudApiException(409, "hash_epoch_ahead",
                $"каталог опубликован в FHV{fhv}, плагин поддерживает FHV{FamilyContentHashFormat.CurrentVersion}. " +
                "[Action: обновите SmartCon до последней версии]");
        }

        var cacheDirectory = _cacheDirectory;
        var objects = await DownloadMissingObjectsAsync(latest.Manifest, cacheDirectory, progress, ct).ConfigureAwait(false);

        // Apply в чистую временную папку → swap: подписная копия либо старая, либо новая целиком.
        var stagingRoot = request.TargetRoot + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            progress?.Report(new CloudOperationProgress("Apply", 0, 1));
            // remote_source_json (V39): аплаер — единственный writer копии, поэтому
            // дубль CloudLink для self-heal пишется в момент сборки.
            var remoteSource = new CloudLink(
                CloudLinkRole.Subscribed,
                _api.CurrentAccount!.Endpoint,
                latest.Manifest.CatalogId,
                request.Slug,
                latest.PublishSeq);
            var applyResult = await _applier.ApplyAsync(latest.Manifest, objects,
                new CatalogManifestApplyOptions
                {
                    DatabaseRootPath = stagingRoot,
                    DatabaseName = request.DatabaseName,
                    RemoteSourceJson = CloudLinkJson.Serialize(remoteSource)
                },
                ct).ConfigureAwait(false);

            SwapDirectories(request.TargetRoot, stagingRoot);
            WriteSyncState(request.TargetRoot, latest.PublishSeq);
            SmartConLogger.Info(
                $"Synced #{latest.PublishSeq}: {applyResult.Items} item(s) → '{Path.GetFileName(request.TargetRoot)}'");
            return new CloudSyncResult(latest.PublishSeq, applyResult.Items, Updated: true, request.TargetRoot);
        }
        catch
        {
            TryDeleteDirectory(stagingRoot);
            throw;
        }
    }

    /// <summary>Качает только недостающие объекты; кэш переживает sync (E1 — докачка при обрыве).</summary>
    private async Task<CacheObjectSource> DownloadMissingObjectsAsync(
        CatalogManifestV1 manifest, string cacheDirectory,
        IProgress<CloudOperationProgress>? progress, CancellationToken ct)
    {
        var source = new CacheObjectSource(cacheDirectory, _api);
        var hashes = CollectObjectHashes(manifest);
        var downloaded = 0;
        for (var i = 0; i < hashes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (source.IsCached(hashes[i])) continue;
            downloaded++;
            progress?.Report(new CloudOperationProgress("Download", downloaded, hashes.Count));
            await source.EnsureDownloadedAsync(hashes[i], ct).ConfigureAwait(false);
        }
        if (downloaded > 0) SmartConLogger.Info($"Downloaded {downloaded} new CAS object(s), {hashes.Count - downloaded} cached");
        return source;
    }

    private static List<string> CollectObjectHashes(CatalogManifestV1 manifest)
    {
        var hashes = new List<string>();
        foreach (var item in manifest.Items)
        {
            if (item.Avatar is not null) hashes.Add(item.Avatar.Sha256);
            foreach (var asset in item.Assets) hashes.Add(asset.Sha256);
            foreach (var version in item.Versions) hashes.Add(version.File.Sha256);
        }
        return hashes;
    }

    private static void SwapDirectories(string targetRoot, string stagingRoot)
    {
        var backupRoot = targetRoot + ".old-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(targetRoot))
        {
            Directory.Move(targetRoot, backupRoot);
            try
            {
                Directory.Move(stagingRoot, targetRoot);
                TryDeleteDirectory(backupRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Новый не встал — возвращаем старый на место (копия не теряется).
                if (Directory.Exists(targetRoot)) TryDeleteDirectory(targetRoot);
                Directory.Move(backupRoot, targetRoot);
                throw;
            }
        }
        else
        {
            Directory.Move(stagingRoot, targetRoot);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SmartConLogger.Warn(
                $"Cannot delete leftover directory '{Path.GetFileName(path)}' ({ex.GetType().Name}). " +
                "[Action: не критично — удалите вручную при нехватке места]");
        }
    }

    private SyncState? ReadSyncState(string targetRoot)
    {
        try
        {
            var path = Path.Combine(targetRoot, SyncStateFileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<SyncState>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private void WriteSyncState(string targetRoot, long publishSeq)
    {
        File.WriteAllText(Path.Combine(targetRoot, SyncStateFileName),
            JsonSerializer.Serialize(new SyncState(publishSeq, _clock.UtcNow)));
    }

    private const string SyncStateFileName = "sync-state.json";

    private sealed record SyncState(long PublishSeq, DateTimeOffset SyncedAtUtc);
}

/// <summary>
/// CAS-кэш подписчика: %APPDATA%\SmartCon\FamilyManager\cloud-cache\objects\{sha[..2]}\{sha256}.
/// Скачанный объект переживает sync-сеансы (E1); отдаётся applier'у как ICloudObjectSource.
/// </summary>
internal sealed class CacheObjectSource : ICloudObjectSource
{
    private readonly string _cacheDirectory;
    private readonly CloudCatalogApiClient _api;

    public CacheObjectSource(string cacheDirectory, CloudCatalogApiClient api)
    {
        _cacheDirectory = cacheDirectory;
        _api = api;
    }

    public bool IsCached(string sha256) => File.Exists(PathOf(sha256));

    public async Task EnsureDownloadedAsync(string sha256, CancellationToken ct = default)
    {
        var path = PathOf(sha256);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var source = await _api.DownloadFileAsync(sha256, ct).ConfigureAwait(false);
        // Уникальный temp: два параллельных download одного объекта не столкнутся
        // на общем .tmp; Move без overwrite → объект либо наш, либо уже скачанный
        // конкурентом (контент одинаковый по определению CAS).
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await source.CopyToAsync(target, 81920, ct).ConfigureAwait(false);
            }
            try
            {
                File.Move(tempPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Кто-то ещё положил объект первым — наш temp лишний.
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException)
            {
                // temp-мусор в кэше не критичен
            }
        }
    }

    public Task<Stream> OpenReadAsync(string sha256, CancellationToken ct = default)
    {
        var path = PathOf(sha256);
        if (!File.Exists(path))
            throw new FileNotFoundException($"CAS object not in cache: {sha256}", path);
        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
    }

    /// <summary>net48-совместимая проверка hex-символа (char.IsAsciiHexDigit — только net7+).</summary>
    private static bool IsAsciiHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>64-hex гвард: недопустимый хэш никогда не попадает в путь (path traversal).</summary>
    private string PathOf(string sha256)
    {
        if (string.IsNullOrEmpty(sha256) || sha256.Length != 64 || !sha256.All(IsAsciiHexDigit))
            throw new ArgumentException($"Invalid sha256: '{sha256}'", nameof(sha256));
        var normalized = sha256.ToLowerInvariant();
        return Path.Combine(_cacheDirectory, normalized[..2], normalized);
    }
}

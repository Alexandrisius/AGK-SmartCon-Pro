using System.IO;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Models.Cloud;

namespace SmartCon.FamilyManager.Services.Cloud;

/// <summary>Прогресс облачной операции для modeless-диалога (ADR-048).</summary>
public sealed record CloudOperationProgress(string Stage, int Current, int Total);

public sealed record CloudPublishRequest(string Slug, string CatalogId, string? MinPluginVersion = null);

public sealed record CloudPublishResult(long PublishSeq, int ItemsCount, int UploadedFiles);

/// <summary>
/// «Опубликовать изменения» (ADR-077 §2): pull-before-push (manifest/latest) →
/// сборка манифеста активного каталога (только активные версии §5) → upload
/// недостающих CAS-объектов (по 422 missing_objects) → POST publish. Атомарный
/// seq-гейт сервера: конкурентная публикация другого автора → 409 stale_base_seq
/// с подсказкой повторить.
/// </summary>
public sealed class CloudPublishService
{
    /// <summary>Максимальное число циклов «422 → upload → publish» (валидатор Фазы 4, P3).</summary>
    private const int MaxUploadRounds = 3;

    private readonly CloudCatalogApiClient _api;
    private readonly CatalogManifestBuilder _builder;
    private readonly IClock _clock;

    public CloudPublishService(CloudCatalogApiClient api, CatalogManifestBuilder builder, IClock clock)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<CloudPublishResult> PublishAsync(
        CloudPublishRequest request,
        IProgress<CloudOperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var account = _api.CurrentAccount ?? throw new CloudApiException(401, "no_session",
            "нет активной облачной сессии — войдите в систему");

        using var _scope = SmartConLogger.BeginScope("CloudPublish",
            ("Method", nameof(PublishAsync)),
            ("Slug", request.Slug),
            ("CatalogId", request.CatalogId));

        // Pull-before-push: seq нового publish point = серверный + 1 (ADR-077 §2).
        var latest = await _api.GetLatestManifestAsync(request.Slug, ct).ConfigureAwait(false);
        var serverSeq = latest?.PublishSeq ?? 0;

        // E7-гейт среза: hash-эпоха сервера не должна опережать локальную (rehash-точки — C4).
        if (latest?.Manifest.HashFormatVersion is { } serverFhv && serverFhv > CurrentHashFormatVersion())
        {
            throw new CloudApiException(409, "hash_epoch_ahead",
                $"сервер хранит манифест FHV{serverFhv}, плагин поддерживает FHV{CurrentHashFormatVersion()}. " +
                "[Action: обновите SmartCon до последней версии и повторите публикацию]");
        }

        progress?.Report(new CloudOperationProgress("BuildManifest", 0, 1));
        var manifest = await _builder.BuildAsync(new CatalogManifestBuildOptions
        {
            CatalogId = request.CatalogId,
            PublishSeq = serverSeq + 1,
            PublishedBy = account.DisplayName,
            MinPluginVersion = request.MinPluginVersion,
        }, ct).ConfigureAwait(false);

        // Publish → при 422 missing_objects: докачиваем недостающие из локального
        // каталога и повторяем (файлы иммутабельны — повторный upload идемпотентен).
        // Лимит раундов страхует от сервера, стабильно отвергающего уже залитое.
        var uploaded = 0;
        for (var round = 1; ; round++)
        {
            ct.ThrowIfCancellationRequested();
            CloudApiException publishError;
            try
            {
                var seq = await _api.PublishAsync(request.Slug, manifest, ct).ConfigureAwait(false);
                SmartConLogger.Info($"Published #{seq}: {manifest.Items.Count} item(s), {uploaded} file(s) uploaded");
                return new CloudPublishResult(seq, manifest.Items.Count, uploaded);
            }
            catch (CloudApiException ex) when (ex.StatusCode == 422
                && string.Equals(ex.Code, "missing_objects", StringComparison.Ordinal))
            {
                publishError = ex;
            }

            if (round >= MaxUploadRounds)
            {
                throw new CloudApiException(422, "upload_rounds_exceeded",
                    $"сервер продолжает требовать объекты после {MaxUploadRounds} раундов загрузки. " +
                    "[Action: проверьте целостность локального каталога и повторите публикацию позже]");
            }

            var missing = publishError.MissingObjects ?? [];
            if (missing.Count == 0) throw publishError;
            var localPaths = CollectLocalPaths(manifest);
            uploaded += await UploadMissingAsync(missing, localPaths, progress, ct).ConfigureAwait(false);
        }
    }

    private async Task<int> UploadMissingAsync(
        IReadOnlyList<string> missing,
        IReadOnlyDictionary<string, string> localPaths,
        IProgress<CloudOperationProgress>? progress,
        CancellationToken ct)
    {
        var uploaded = 0;
        for (var i = 0; i < missing.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new CloudOperationProgress("Upload", i + 1, missing.Count));
            if (!localPaths.TryGetValue(missing[i], out var path))
            {
                throw new CloudApiException(422, "local_object_missing",
                    $"сервер требует объект {missing[i]}, но локальный файл не найден. " +
                    "[Action: переимпортируйте семейство или удалите битую версию в каталоге]");
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await _api.UploadFileAsync(missing[i], stream, ct).ConfigureAwait(false);
            uploaded++;
        }
        return uploaded;
    }

    /// <summary>sha256 → локальный путь всех объектов манифеста (файлы версий, ассеты, аватары).</summary>
    private static IReadOnlyDictionary<string, string> CollectLocalPaths(CatalogManifestV1 manifest)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Items)
        {
            if (item.Avatar?.LocalPath is not null) map[item.Avatar.Sha256] = item.Avatar.LocalPath;
            foreach (var asset in item.Assets)
                if (asset.LocalPath is not null) map[asset.Sha256] = asset.LocalPath;
            foreach (var version in item.Versions)
                if (version.File.LocalPath is not null) map[version.File.Sha256] = version.File.LocalPath;
        }
        return map;
    }

    private static int CurrentHashFormatVersion() => FamilyContentHashFormat.CurrentVersion;
}

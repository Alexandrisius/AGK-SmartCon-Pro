using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.FamilyManager.Models.Cloud;

namespace SmartCon.FamilyManager.Services.Cloud;

/// <summary>
/// Контент-дайджест манифеста: волатильные поля publish-точки (seq, время,
/// автор, minPluginVersion) исключены — дайджест меняется только при изменении
/// контента каталога. Основа точки «есть локальные непубликованные изменения».
/// </summary>
internal static class CatalogManifestFingerprint
{
    public static string Compute(CatalogManifestV1 manifest)
    {
        var content = new
        {
            format = manifest.Format,
            formatVersion = manifest.FormatVersion,
            catalogId = manifest.CatalogId,
            hashFormatVersion = manifest.HashFormatVersion,
            revitVersionRange = manifest.RevitVersionRange,
            meta = manifest.Meta,
            items = manifest.Items,
            removed = manifest.Removed,
        };
        var json = JsonSerializer.Serialize(content, CatalogManifestJson.WriteCompact);
        // Convert.ToHexStringLower — только .NET 9+; плагин — net8/net48.
#if NET8_0_OR_GREATER
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
#else
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(json));
        var sb = new System.Text.StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
#endif
    }
}

/// <summary>
/// Локальное состояние «что уже опубликовано» для Published-базы (точка на
/// шестерёнке: не забудь опубликовать после локальных правок — владелец
/// 2026-09-11). После publish пишет дайджест манифеста; проверка — пересборка
/// манифеста активной базы и сравнение дайджестов. Отсутствие файла = точка
/// («с этой машины не публиковали» / файл потерян — безопасно перевыпубликовать:
/// publish идемпотентен по контенту).
/// </summary>
public sealed class CloudPublishStateService
{
    private readonly CatalogManifestBuilder _builder;
    private readonly string _stateDir;

    public CloudPublishStateService(CatalogManifestBuilder builder) : this(builder, Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SmartCon", "FamilyManager", "cloud", "published"))
    {
    }

    internal CloudPublishStateService(CatalogManifestBuilder builder, string stateDir)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _stateDir = stateDir;
    }

    public void RecordPublished(string slug, CatalogManifestV1 manifest)
    {
        Directory.CreateDirectory(_stateDir);
        File.WriteAllText(Path.Combine(_stateDir, slug + ".fingerprint"),
            CatalogManifestFingerprint.Compute(manifest));
    }

    public void Clear(string slug)
    {
        try
        {
            File.Delete(Path.Combine(_stateDir, slug + ".fingerprint"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SmartConLogger.Debug($"Publish-state clear failed for '{slug}': {ex.Message}");
        }
    }

    /// <summary>
    /// true = контент активной базы отличается от последней публикации (или
    /// никогда не публиковали с этой машины). Работает по АКТИВНОЙ базе —
    /// builder читает текущий каталог.
    /// </summary>
    public async Task<bool> HasUnpublishedChangesAsync(string slug, string catalogId, CancellationToken ct = default)
    {
        var stored = TryReadFingerprint(slug);
        if (stored is null) return true;

        var manifest = await _builder.BuildAsync(new CatalogManifestBuildOptions
        {
            CatalogId = catalogId,
            PublishSeq = 0,
            PublishedBy = string.Empty,
        }, ct).ConfigureAwait(false);
        return !string.Equals(CatalogManifestFingerprint.Compute(manifest), stored, StringComparison.Ordinal);
    }

    private string? TryReadFingerprint(string slug)
    {
        try
        {
            var path = Path.Combine(_stateDir, slug + ".fingerprint");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

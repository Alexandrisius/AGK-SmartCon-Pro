using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartCon.Core.Logging;
using SmartCon.FamilyManager.Models.Cloud;

namespace SmartCon.FamilyManager.Services.Cloud;

/// <summary>
/// Контент-дайджест манифеста. Из дайджеста исключено всё волатильное:
/// поля publish-точки (seq, время, автор, minPluginVersion) И битовый блок
/// <c>file</c> (sha256/sizeBytes/fileName) — Revit пересохраняет .rfa
/// рандомно БЕЗ изменения содержимого, поэтому детекция изменений ведётся по
/// FHV contentHash из БД (владелец 2026-09-11). Битовый sha остаётся только
/// адресом CAS-объекта на хранении/транспорте. Основа точки «есть локальные
/// непубликованные изменения» и дельты sync.
/// </summary>
internal static class CatalogManifestFingerprint
{
    public static string Compute(CatalogManifestV1 manifest)
    {
        var root = JsonNode.Parse(JsonSerializer.Serialize(manifest, CatalogManifestJson.WriteCompact));
        // Волатильные поля publish-точки: publish-time build (seq=N, автор,
        // время) и check-time build (seq=0, пустой автор) одного контента
        // обязаны давать одинаковый дайджест.
        if (root is JsonObject top)
        {
            top.Remove("publishSeq");
            top.Remove("publishedAtUtc");
            top.Remove("publishedBy");
            top.Remove("minPluginVersion");
        }
        StripByteLevelFileRefs(root);
        return ComputeHash(root!.ToJsonString(CatalogManifestJson.WriteCompact));
    }

    /// <summary>Per-item дайджест для дельты sync (тот же контент-вид, что у Compute).</summary>
    public static string ComputeItemDigest(ManifestItemV1 item)
    {
        var root = JsonNode.Parse(JsonSerializer.Serialize(item, CatalogManifestJson.WriteCompact));
        StripByteLevelFileRefs(root);
        return ComputeHash(root!.ToJsonString(CatalogManifestJson.WriteCompact));
    }

    /// <summary>Рекурсивно вырезает свойство "file" (битовый sha/размер/имя файла)
    /// — контент версии решает contentHash. Обходит и объекты, и массивы.</summary>
    private static void StripByteLevelFileRefs(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("file");
                foreach (var child in obj.ToList())
                    StripByteLevelFileRefs(child.Value);
                break;
            case JsonArray array:
                foreach (var element in array.ToList())
                    StripByteLevelFileRefs(element);
                break;
        }
    }

    /// <summary>SHA-256 hex (lowercase) строки. Convert.ToHexStringLower — только .NET 9+; плагин — net8/net48.</summary>
    public static string ComputeHash(string text)
    {
#if NET8_0_OR_GREATER
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
#else
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
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
            var path = Path.Combine(_stateDir, slug + ".fingerprint");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SmartConLogger.Debug($"Publish-state clear failed for '{slug}': {ex.Message}");
        }
    }

    /// <summary>
    /// true = контент активной базы отличается от последней публикации. Работает
    /// по АКТИВНОЙ базе — builder читает текущий каталог. Никогда не
    /// публиковали: точка горит только если публиковать ЕСТЬ что (пустой
    /// каталог владельца 2026-09-11: точка на пустой базе — шум). Публикованный
    /// каталог, опустевший локально, — честное «есть изменения» (было 8 → 0).
    /// </summary>
    public async Task<bool> HasUnpublishedChangesAsync(string slug, string catalogId, CancellationToken ct = default)
    {
        var stored = TryReadFingerprint(slug);

        var manifest = await _builder.BuildAsync(new CatalogManifestBuildOptions
        {
            CatalogId = catalogId,
            PublishSeq = 0,
            PublishedBy = string.Empty,
        }, ct).ConfigureAwait(false);

        // Пустой каталог без публикаций — публиковать нечего.
        if (stored is null && manifest.Items.Count == 0)
            return false;
        if (stored is null)
            return true;

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

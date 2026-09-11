using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Models.Cloud;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Cloud;

/// <summary>Целевая локация cloud-копии (строится sync-флоу во временной папке с последующим swap).</summary>
public sealed class CatalogManifestApplyOptions
{
    /// <summary>Корень новой базы (папка, в которой будет создан catalog.db). Должна быть пустой.</summary>
    public string DatabaseRootPath { get; init; } = string.Empty;

    /// <summary>Display name базы у подписчика (имя каталога/slug из приглашения).</summary>
    public string DatabaseName { get; init; } = string.Empty;
}

public sealed class CatalogManifestApplyResult
{
    public int Items { get; set; }
    public int Versions { get; set; }
    public int FilesWritten { get; set; }
    public int AssetsWritten { get; set; }
}

/// <summary>
/// Применяет манифест v1 к чистой cloud-копии каталога: полная схема V38
/// через мигратор, стабильные item guid из манифеста, новые локальные id
/// для versions/files/types/runs, файлы из CAS по SHA-256. DI-singleton
/// LocalCatalogDatabase (активная база) не затрагивается — применяется
/// через приватный экземпляр на целевом корне.
/// </summary>
internal sealed partial class CatalogManifestApplier
{
    private readonly IClock _clock;

    public CatalogManifestApplier(IClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<CatalogManifestApplyResult> ApplyAsync(
        CatalogManifestV1 manifest,
        ICloudObjectSource objects,
        CatalogManifestApplyOptions options,
        CancellationToken ct = default)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));
        if (objects is null) throw new ArgumentNullException(nameof(objects));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.DatabaseRootPath))
            throw new ArgumentException("DatabaseRootPath обязателен", nameof(options));
        if (!string.Equals(manifest.Format, "smartcon.cloud.catalog-manifest", StringComparison.Ordinal))
            throw new ArgumentException($"Неизвестный формат манифеста: '{manifest.Format}'");
        if (manifest.FormatVersion != 1)
            throw new NotSupportedException(
                $"Манифест formatVersion={manifest.FormatVersion} не поддерживается этой версией плагина. " +
                "[Action: обновите SmartCon до последней версии]");

        using var _scope = SmartConLogger.BeginScope("CloudApply",
            ("Method", nameof(ApplyAsync)),
            ("CatalogId", manifest.CatalogId),
            ("PublishSeq", manifest.PublishSeq));

        var result = new CatalogManifestApplyResult();
        var now = _clock.UtcNow;

        // apply = rebuild: целевой корень обязан быть пуст (sync строит во временной папке
        // и свапает целиком); повторный apply поверх остатков даст UNIQUE-краш вместо ясной ошибки.
        if (Directory.Exists(options.DatabaseRootPath) && Directory.EnumerateFileSystemEntries(options.DatabaseRootPath).Any())
            throw new InvalidOperationException(
                $"Целевой корень не пуст: '{options.DatabaseRootPath}'. [Action: sync обязан строить копию в чистой временной папке]");

        // Приватная база на целевом корне: DI-singleton активного каталога не мутируем.
        var database = new LocalCatalogDatabase();
        database.SwitchToPath(options.DatabaseRootPath);
        var migrator = new LocalCatalogMigrator(database);
        await migrator.MigrateAsync(ct).ConfigureAwait(false);

        using var connection = database.CreateWritableConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();

        await InsertDatabaseMetaAsync(connection, tx, manifest, options, now, ct).ConfigureAwait(false);
        var categoryPathToId = await InsertMetaAsync(connection, tx, manifest.Meta, now, ct).ConfigureAwait(false);

        // Двухпроходная вставка: family_dependencies ссылаются на ДРУГИЕ items — все
        // catalog_items должны существовать до вставки версий с зависимостями (FK immediate).
        foreach (var item in manifest.Items)
        {
            await InsertItemAsync(connection, tx, database, item, objects,
                categoryPathToId, manifest.HashFormatVersion, now, result, ct).ConfigureAwait(false);
        }
        foreach (var item in manifest.Items)
        {
            foreach (var version in item.Versions)
            {
                await InsertVersionAsync(connection, tx, database, item, version, objects,
                    manifest.HashFormatVersion, now, result, ct).ConfigureAwait(false);
            }
        }

        tx.Commit();
        SmartConLogger.Info(
            $"Manifest applied: {result.Items} item(s), {result.Versions} version(s), " +
            $"{result.FilesWritten} file(s), {result.AssetsWritten} asset(s) written to '{Path.GetFileName(options.DatabaseRootPath)}'");
        return result;
    }

    private static async Task InsertDatabaseMetaAsync(
        SqliteConnection connection, SqliteTransaction tx,
        CatalogManifestV1 manifest, CatalogManifestApplyOptions options,
        DateTimeOffset now, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO database_meta (id, name, description, created_at_utc, schema_version, min_plugin_version)
            VALUES (@id, @name, NULL, @now, 2, @minPlugin)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString("N")));
        cmd.Parameters.Add(new SqliteParameter("@name",
            string.IsNullOrWhiteSpace(options.DatabaseName) ? manifest.CatalogId : options.DatabaseName));
        cmd.Parameters.Add(new SqliteParameter("@now", now.ToString("o")));
        cmd.Parameters.Add(new SqliteParameter("@minPlugin",
            (object?)manifest.MinPluginVersion ?? DbCompatibility.CurrentMinPluginVersion));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertItemAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        LocalCatalogDatabase database,
        ManifestItemV1 item,
        ICloudObjectSource objects,
        Dictionary<string, string> categoryPathToId,
        int? hashFormatVersion,
        DateTimeOffset now,
        CatalogManifestApplyResult result,
        CancellationToken ct)
    {
        categoryPathToId.TryGetValue(item.CategoryPath, out var categoryId);

        // catalog_items.content_hash/fmt зеркалируют активную версию (как SetActiveVersion).
        var activeVersion = item.Versions.OrderByDescending(v => v.SourceRevitVersion).FirstOrDefault();

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO catalog_items (id, name, normalized_name, description, category_id,
                                           content_status, current_version_label, published_by,
                                           family_source, revit_category, revit_category_id,
                                           content_hash, hash_format_version, created_at_utc, updated_at_utc)
                VALUES (@id, @name, @norm, @description, @categoryId,
                        @status, @currentLabel, NULL,
                        @source, @revitCategory, @revitCategoryId,
                        @contentHash, @fmt, @createdAt, @updatedAt)
                """;
            var stamp = activeVersion?.PublishedAtUtc ?? now;
            cmd.Parameters.Add(new SqliteParameter("@id", item.Id));
            cmd.Parameters.Add(new SqliteParameter("@name", item.Name));
            cmd.Parameters.Add(new SqliteParameter("@norm", string.IsNullOrEmpty(item.NormalizedName) ? item.Name : item.NormalizedName));
            cmd.Parameters.Add(new SqliteParameter("@description", (object?)item.Description ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@categoryId", (object?)categoryId ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@status", item.ContentStatus));
            cmd.Parameters.Add(new SqliteParameter("@currentLabel", item.CurrentVersionLabel));
            cmd.Parameters.Add(new SqliteParameter("@source", item.FamilySource));
            cmd.Parameters.Add(new SqliteParameter("@revitCategory", (object?)item.RevitCategory ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@revitCategoryId", (object?)item.RevitCategoryId ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@contentHash", (object?)activeVersion?.ContentHash ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@fmt", (object?)hashFormatVersion ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@createdAt", stamp.ToString("o")));
            cmd.Parameters.Add(new SqliteParameter("@updatedAt", stamp.ToString("o")));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await InsertItemDetailsAsync(connection, tx, item, now, ct).ConfigureAwait(false);
        result.AssetsWritten += await InsertAssetsAsync(connection, tx, database, item, objects, now, ct).ConfigureAwait(false);
        result.Items++;
    }

    private static async Task InsertVersionAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        LocalCatalogDatabase database,
        ManifestItemV1 item,
        ManifestVersionV1 version,
        ICloudObjectSource objects,
        int? hashFormatVersion,
        DateTimeOffset now,
        CatalogManifestApplyResult result,
        CancellationToken ct)
    {
        var versionId = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString();
        // Несколько Revit-вариантов одного label обязаны различаться путями —
        // иначе второй файл затирает первого. r{revit}-подкаталог — legacy-совместимый
        // формат StoragePathResolver.GetRfaFilePath(id, label, revit, name); resolver
        // читает путь целиком из БД, так что оба варианта валидны.
        var variantCount = item.Versions.Count(v => v.VersionLabel == version.VersionLabel);
        var relativePath = variantCount > 1
            ? $"files/{item.Id}/{version.VersionLabel}/r{version.SourceRevitVersion}/{version.File.FileName}"
            : $"files/{item.Id}/{version.VersionLabel}/{version.File.FileName}";
        var absolutePath = Path.Combine(database.GetDatabaseRoot(), relativePath);
        // Fail-fast: sync-флоу обязан гарантировать полный CAS-кэш до apply (E1);
        // недостающий объект откатывает всю транзакцию — копия всегда = манифесту.
        if (!await CopyObjectAsync(objects, version.File, absolutePath, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"CAS object '{version.File.Sha256}' (version '{version.VersionLabel}' of '{item.Name}') " +
                "unavailable or size mismatch — apply aborted, transaction rolled back");
        }
        result.FilesWritten++;

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
                VALUES (@id, @path, @name, @revit, @now)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", fileId));
            cmd.Parameters.Add(new SqliteParameter("@path", relativePath));
            cmd.Parameters.Add(new SqliteParameter("@name", version.File.FileName));
            cmd.Parameters.Add(new SqliteParameter("@revit", version.SourceRevitVersion));
            cmd.Parameters.Add(new SqliteParameter("@now", now.ToString("o")));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, revit_major_version,
                                              types_count, parameters_count, content_hash, hash_format_version,
                                              glb_state, section_hashes, section_strings, routing_backfilled,
                                              published_at_utc, published_by)
                VALUES (@id, @itemId, @fileId, @label, @revit,
                        @typesCount, @paramsCount, @hash, @fmt,
                        @glbState, @sectionHashes, @sectionStrings, @backfilled,
                        @publishedAt, @publishedBy)
                """;
            var itemFmt = hashFormatVersion;
            cmd.Parameters.Add(new SqliteParameter("@id", versionId));
            cmd.Parameters.Add(new SqliteParameter("@itemId", item.Id));
            cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
            cmd.Parameters.Add(new SqliteParameter("@label", version.VersionLabel));
            cmd.Parameters.Add(new SqliteParameter("@revit", version.SourceRevitVersion));
            cmd.Parameters.Add(new SqliteParameter("@typesCount", (object?)version.TypesCount ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@paramsCount", (object?)version.ParametersCount ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@hash", (object?)version.ContentHash ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@fmt", (object?)itemFmt ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@glbState", (object?)version.GlbState ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@sectionHashes",
                (object?)SerializeDictionary(version.SectionHashes) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@sectionStrings",
                (object?)SerializeDictionary(version.SectionStrings) ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@backfilled", (object?)version.RoutingBackfilled ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@publishedAt",
                (version.PublishedAtUtc ?? now).ToString("o")));
            cmd.Parameters.Add(new SqliteParameter("@publishedBy", (object?)version.PublishedBy ?? DBNull.Value));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await InsertVersionDetailsAsync(connection, tx, item.Id, versionId, fileId, version, now, ct).ConfigureAwait(false);
        result.Versions++;
    }

    /// <summary>Копирует CAS-объект в managed storage, проверяя длину (лёгкая верификация среза).</summary>
    private static async Task<bool> CopyObjectAsync(
        ICloudObjectSource objects, ManifestFileRefV1 fileRef, string absolutePath, CancellationToken ct)
    {
        var ok = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            // sync-using: net48 не имеет IAsyncDisposable у Stream/FileStream.
            using (var source = await objects.OpenReadAsync(fileRef.Sha256, ct).ConfigureAwait(false))
            using (var target = new FileStream(absolutePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                long total = 0;
                var buffer = new byte[81920];
                int read;
#if NET8_0_OR_GREATER
                while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;
                }
#else
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                    total += read;
                }
#endif
                ok = total == fileRef.SizeBytes;
            }
            if (!ok) TryDeleteFile(absolutePath);
            return ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            TryDeleteFile(absolutePath);
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Недописанный битый файл не должен ломать apply — следующий sync перезапишет.
        }
    }

    private static string? SerializeDictionary(Dictionary<string, string>? map)
    {
        // Тот же формат, что ContentSectionJsonSerializer (плоский Dictionary<string,string>).
        return map is null || map.Count == 0 ? null : JsonSerializer.Serialize(map);
    }
}

using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed partial class LocalCatalogProvider : IFamilyCatalogProvider, IWritableFamilyCatalogProvider
{
    private readonly LocalCatalogDatabase _database;

    public LocalCatalogProvider(LocalCatalogDatabase database)
    {
        _database = database;
    }

    public Task<FamilyImportResult> ImportAsync(FamilyImportRequest request, CancellationToken ct = default)
    {
        throw new NotSupportedException("Use IFamilyImportService for import operations.");
    }

    public Task<FamilyBatchImportResult> ImportFolderAsync(FamilyFolderImportRequest request, IProgress<FamilyImportProgress>? progress, CancellationToken ct = default)
    {
        throw new NotSupportedException("Use IFamilyImportService for import operations.");
    }

    public async Task<FamilyCatalogItem> UpdateItemAsync(string id, string? name, string? description, string? categoryId, IReadOnlyList<string>? tags, ContentStatus? status, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();

        try
        {
            var setClauses = new List<string>();
            var cmd = connection.CreateCommand();

            if (name is not null)
            {
                setClauses.Add("name = @name");
                setClauses.Add("normalized_name = @normalizedName");
                cmd.Parameters.Add(new SqliteParameter("@name", name));
                cmd.Parameters.Add(new SqliteParameter("@normalizedName", Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(name)));
            }

            if (description is not null)
            {
                setClauses.Add("description = @description");
                cmd.Parameters.Add(new SqliteParameter("@description", description));
            }

            setClauses.Add("category_id = @categoryId");
            cmd.Parameters.Add(new SqliteParameter("@categoryId", (object?)categoryId ?? DBNull.Value));

            if (status is not null)
            {
                setClauses.Add("content_status = @status");
                cmd.Parameters.Add(new SqliteParameter("@status", status.Value.ToString()));
            }

            setClauses.Add("updated_at_utc = @updatedAtUtc");
            cmd.Parameters.Add(new SqliteParameter("@updatedAtUtc", DateTimeOffset.UtcNow.ToString("o")));
            cmd.Parameters.Add(new SqliteParameter("@id", id));

            cmd.CommandText = $"UPDATE catalog_items SET {string.Join(", ", setClauses)} WHERE id = @id";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            if (tags is not null)
            {
                using var delCmd = connection.CreateCommand();
                delCmd.CommandText = "DELETE FROM catalog_tags WHERE catalog_item_id = @id";
                delCmd.Parameters.Add(new SqliteParameter("@id", id));
                await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                foreach (var tag in tags)
                {
                    var normalizedTag = Core.Services.FamilyManager.FamilySearchNormalizer.Normalize(tag);
                    using var tagCmd = connection.CreateCommand();
                    tagCmd.CommandText = "INSERT OR IGNORE INTO catalog_tags (catalog_item_id, tag, normalized_tag) VALUES (@id, @tag, @normalizedTag)";
                    tagCmd.Parameters.Add(new SqliteParameter("@id", id));
                    tagCmd.Parameters.Add(new SqliteParameter("@tag", tag));
                    tagCmd.Parameters.Add(new SqliteParameter("@normalizedTag", normalizedTag));
                    await tagCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return (await GetItemAsync(id, ct).ConfigureAwait(false))!;
    }

    public async Task<bool> DeleteItemAsync(string id, CancellationToken ct = default)
    {
        var dbRoot = _database.GetDatabaseRoot();
        var familyDir = Path.Combine(dbRoot, "files", id);
        var dirExists = Directory.Exists(familyDir);

        // Attempt file deletion BEFORE database transaction.
        // If files are locked, exception surfaces here and DB record remains intact.
        if (dirExists)
        {
            await DeleteDirectoryWithRetryAsync(familyDir, ct).ConfigureAwait(false);
        }

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA foreign_keys = ON";
            await pragmaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        int rowsAffected;
        using var tx = connection.BeginTransaction();
        try
        {
            using var delItem = connection.CreateCommand();
            delItem.CommandText = "DELETE FROM catalog_items WHERE id = @id";
            delItem.Parameters.Add(new SqliteParameter("@id", id));
            rowsAffected = await delItem.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return rowsAffected > 0;
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path, CancellationToken ct, int maxRetries = 5)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    await Task.Run(() =>
                    {
                        RemoveReadOnlyAttributes(path);
                        Directory.Delete(path, recursive: true);
                    }, ct);
                }
                return;
            }
            catch (IOException ex) when (i < maxRetries - 1)
            {
                using var _scope = SmartConLogger.BeginScope("FM Delete", ("Path", path), ("Attempt", i + 1));
                SmartConLogger.Warn($"failed to delete directory: {ex.Message}. Retrying... [Action: обычно файл заблокирован антивирусом или другим процессом; операция будет повторена до 5 раз]");
                await Task.Delay(200 * (i + 1), ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex) when (i < maxRetries - 1)
            {
                using var _scope = SmartConLogger.BeginScope("FM Delete", ("Path", path), ("Attempt", i + 1));
                SmartConLogger.Warn($"failed (access denied): {ex.Message}. Retrying... [Action: обычно файл заблокирован антивирусом или другим процессом; операция будет повторена до 5 раз]");
                await Task.Delay(200 * (i + 1), ct).ConfigureAwait(false);
            }
        }

        // Final attempt: force GC to release any lingering WPF image handles before last try
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        try
        {
            if (Directory.Exists(path))
            {
                await Task.Run(() =>
                {
                    RemoveReadOnlyAttributes(path);
                    Directory.Delete(path, recursive: true);
                }, ct);
            }
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to delete family directory after {maxRetries} attempts: {path}. The file may be open in Revit or another application. {ex.Message}");
        }
    }

    private static void RemoveReadOnlyAttributes(string path)
    {
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            var attr = File.GetAttributes(file);
            if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
            }
        }
    }

    public FamilyCatalogCapabilities GetCapabilities() => new(
        SupportsWrite: true,
        SupportsSearch: true,
        SupportsTags: true,
        SupportsBatchImport: true,
        SupportsVersionHistory: true,
        ProviderKind: CatalogProviderKind.Local);

    public async Task<IReadOnlyList<FamilyCatalogItem>> SearchAsync(FamilyCatalogQuery query, CancellationToken ct = default)
    {
        var (whereSql, whereParams) = LocalCatalogQueryBuilder.BuildWhereClause(query);
        var orderBy = LocalCatalogQueryBuilder.BuildOrderBy(query.Sort);
        var limitOffsetParams = LocalCatalogQueryBuilder.BuildLimitOffsetParameters(query);

        var sql = $"""
            SELECT ci.*,
                (SELECT MIN(cv.revit_major_version) FROM catalog_versions cv
                 WHERE cv.catalog_item_id = ci.id
                   AND cv.version_label = ci.current_version_label) AS active_revit_major_version,
                (SELECT MIN(cv2.revit_major_version) FROM catalog_versions cv2
                 WHERE cv2.catalog_item_id = ci.id) AS min_revit_major_version
            FROM catalog_items ci
            {whereSql}
            {orderBy}
            LIMIT @limit OFFSET @offset
            """;

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        foreach (var p in whereParams)
            cmd.Parameters.Add(p);
        cmd.Parameters.Add(limitOffsetParams[0]);
        cmd.Parameters.Add(limitOffsetParams[1]);

        var items = new List<FamilyCatalogItem>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var item = ReadCatalogItem(reader);
            items.Add(item);
        }

        if (items.Count > 0)
        {
            var tagsMap = await LoadAllTagsBatchAsync(connection, items.Select(i => i.Id).ToList(), ct).ConfigureAwait(false);
            for (var i = 0; i < items.Count; i++)
            {
                tagsMap.TryGetValue(items[i].Id, out var tags);
                tags ??= [];
                var old = items[i];
                items[i] = new FamilyCatalogItem(
                    old.Id,
                    old.Name,
                    old.NormalizedName,
                    old.Description,
                    old.CategoryPath,
                    old.CategoryId,
                    old.Manufacturer,
                    old.ContentStatus,
                    old.CurrentVersionLabel,
                    tags,
                    old.PublishedBy,
                    old.CreatedAtUtc,
                    old.UpdatedAtUtc,
                    old.FamilySource,
                    old.RevitCategory,
                    old.ContentHash,
                    old.HashFormatVersion,
                    old.ActiveRevitMajorVersion,
                    old.MinRevitMajorVersion);
            }
        }

        return items;
    }

    public async Task<FamilyCatalogItem?> GetItemAsync(string id, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM catalog_items WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", id));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var item = ReadCatalogItem(reader);
        var tags = await LoadTagsAsync(connection, id, ct).ConfigureAwait(false);
        return new FamilyCatalogItem(
            item.Id,
            item.Name,
            item.NormalizedName,
            item.Description,
            item.CategoryPath,
            item.CategoryId,
            item.Manufacturer,
            item.ContentStatus,
            item.CurrentVersionLabel,
            tags,
            item.PublishedBy,
            item.CreatedAtUtc,
            item.UpdatedAtUtc,
            item.FamilySource,
            item.RevitCategory,
            item.ContentHash,
            item.HashFormatVersion);
    }

    public async Task<IReadOnlyList<FamilyCatalogVersion>> GetVersionsAsync(string catalogItemId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT cv.*, ff.file_name
            FROM catalog_versions cv
            LEFT JOIN family_files ff ON ff.id = cv.file_id
            WHERE cv.catalog_item_id = @itemId
            ORDER BY cv.published_at_utc DESC
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));

        var versions = new List<FamilyCatalogVersion>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            versions.Add(ReadCatalogVersion(reader));
        }

        return versions;
    }

    public async Task<FamilyFileRecord?> GetFileAsync(string fileId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM family_files WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", fileId));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return ReadFileRecord(reader);
    }

    public async Task<int> GetItemCountAsync(CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM catalog_items";

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long l ? (int)l : 0;
    }

    public async Task<IReadOnlyList<int>> GetAvailableRevitVersionsAsync(string catalogItemId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT cv.revit_major_version
            FROM catalog_versions cv
            INNER JOIN catalog_items ci ON ci.id = cv.catalog_item_id
            WHERE cv.catalog_item_id = @id AND ci.current_version_label = cv.version_label
            ORDER BY cv.revit_major_version DESC
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", catalogItemId));

        var versions = new List<int>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static async Task<IReadOnlyList<string>> LoadTagsAsync(SqliteConnection connection, string catalogItemId, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT tag FROM catalog_tags WHERE catalog_item_id = @itemId ORDER BY tag";
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));

        var tags = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            tags.Add(reader.GetString(0));
        }

        return tags;
    }

    private static async Task<Dictionary<string, List<string>>> LoadAllTagsBatchAsync(
        SqliteConnection connection, IReadOnlyList<string> itemIds, CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>(itemIds.Count);
        if (itemIds.Count == 0) return result;

        var parameters = new string[itemIds.Count];
        for (var i = 0; i < itemIds.Count; i++)
            parameters[i] = $"@id{i}";

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT catalog_item_id, tag FROM catalog_tags WHERE catalog_item_id IN ({string.Join(", ", parameters)}) ORDER BY tag";

        for (var i = 0; i < itemIds.Count; i++)
        {
            cmd.Parameters.Add(new SqliteParameter(parameters[i], itemIds[i]));
            result[itemIds[i]] = [];
        }

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var itemId = reader.GetString(0);
            var tag = reader.GetString(1);
            result[itemId].Add(tag);
        }

        return result;
    }

    internal static FamilyCatalogItem ReadCatalogItem(SqliteDataReader reader)
    {
        var id = reader.GetString(reader.GetOrdinal("id"));
        var name = reader.GetString(reader.GetOrdinal("name"));
        var categoryPath = !reader.IsDBNull(reader.GetOrdinal("category_name"))
            ? reader.GetString(reader.GetOrdinal("category_name"))
            : null;

        var categoryId = TryGetString(reader, "category_id");
        var familySource = TryGetString(reader, "family_source") ?? "loadable";
        var revitCategory = TryGetString(reader, "revit_category");
        var contentHash = TryGetString(reader, "content_hash");
        int? hashFormatVersion = TryGetInt(reader, "hash_format_version");

        return new FamilyCatalogItem(
            Id: reader.GetString(reader.GetOrdinal("id")),
            Name: reader.GetString(reader.GetOrdinal("name")),
            NormalizedName: reader.GetString(reader.GetOrdinal("normalized_name")),
            Description: reader.IsDBNull(reader.GetOrdinal("description"))
                ? null
                : reader.GetString(reader.GetOrdinal("description")),
            CategoryPath: categoryPath,
            CategoryId: categoryId,
            Manufacturer: reader.IsDBNull(reader.GetOrdinal("manufacturer"))
                ? null
                : reader.GetString(reader.GetOrdinal("manufacturer")),
            ContentStatus: ContentStatusParser.Parse(reader.IsDBNull(reader.GetOrdinal("content_status"))
                ? null
                : reader.GetString(reader.GetOrdinal("content_status"))),
            CurrentVersionLabel: reader.IsDBNull(reader.GetOrdinal("current_version_label"))
                ? null
                : reader.GetString(reader.GetOrdinal("current_version_label")),
            Tags: [],
            PublishedBy: reader.IsDBNull(reader.GetOrdinal("published_by"))
                ? null
                : reader.GetString(reader.GetOrdinal("published_by")),
            CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))),
            UpdatedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at_utc"))),
            FamilySource: familySource,
            RevitCategory: revitCategory,
            ContentHash: contentHash,
            HashFormatVersion: hashFormatVersion,
            ActiveRevitMajorVersion: TryGetInt(reader, "active_revit_major_version"),
            MinRevitMajorVersion: TryGetInt(reader, "min_revit_major_version"));
    }

    private static FamilyCatalogVersion ReadCatalogVersion(SqliteDataReader reader) => new(
        Id: reader.GetString(reader.GetOrdinal("id")),
        CatalogItemId: reader.GetString(reader.GetOrdinal("catalog_item_id")),
        FileId: reader.GetString(reader.GetOrdinal("file_id")),
        VersionLabel: reader.GetString(reader.GetOrdinal("version_label")),
        RevitMajorVersion: reader.GetInt32(reader.GetOrdinal("revit_major_version")),
        TypesCount: reader.IsDBNull(reader.GetOrdinal("types_count"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("types_count")),
        ParametersCount: reader.IsDBNull(reader.GetOrdinal("parameters_count"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("parameters_count")),
        PublishedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("published_at_utc"))),
        ContentHash: TryGetString(reader, "content_hash"),
        HashFormatVersion: TryGetInt(reader, "hash_format_version"),
        PublishedBy: TryGetString(reader, "published_by"),
        FileName: TryGetString(reader, "file_name"));

    private static FamilyFileRecord ReadFileRecord(SqliteDataReader reader) => new(
        Id: reader.GetString(reader.GetOrdinal("id")),
        RelativePath: reader.GetString(reader.GetOrdinal("relative_path")),
        FileName: reader.GetString(reader.GetOrdinal("file_name")),
        RevitMajorVersion: reader.GetInt32(reader.GetOrdinal("revit_major_version")),
        ImportedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("imported_at_utc"))));

    public async Task<FamilyCatalogItem?> FindByNormalizedNameAsync(string normalizedName, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM catalog_items WHERE normalized_name = @name LIMIT 1";
        cmd.Parameters.Add(new SqliteParameter("@name", normalizedName));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return ReadCatalogItem(reader);
    }

    public async Task<IReadOnlyList<FamilyCatalogItem>> GetItemsBySourceAsync(string familySource, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM catalog_items WHERE family_source = @source ORDER BY name";
        cmd.Parameters.Add(new SqliteParameter("@source", familySource));

        var items = new List<FamilyCatalogItem>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(ReadCatalogItem(reader));
        }

        return items;
    }

    private static string? TryGetString(SqliteDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i) == columnName && !reader.IsDBNull(i))
                return reader.GetString(i);
        }
        return null;
    }

    private static int? TryGetInt(SqliteDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i) == columnName && !reader.IsDBNull(i))
                return reader.GetInt32(i);
        }
        return null;
    }

    /// <summary>
    /// Cross-version content-hash search. Looks for a matching
    /// <c>content_hash</c> across ALL versions (current and archived) of
    /// ALL catalog items, filtered by <paramref name="familySource"/> to
    /// enforce cross-source separation (system hashes never match
    /// loadable hashes) and by <paramref name="hashFormatVersion"/> so
    /// old-format hashes do not produce false duplicate matches against
    /// new-format ones.
    /// </summary>
    /// <param name="hexHash">SHA-256 hex string.</param>
    /// <param name="hashFormatVersion">Hash format version (must match
    /// <c>hash_format_version</c> column).</param>
    /// <param name="familySource"><c>"loadable"</c> or <c>"system"</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ContentHashMatch"/> if a match was found,
    /// or <c>null</c> if no version has this hash.</returns>
    public async Task<ContentHashMatch?> FindByContentHashAcrossVersionsAsync(
        string hexHash,
        int hashFormatVersion,
        string familySource,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(hexHash))
            return null;

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        // Issue #126: also select the matched item's name/normalized_name/
        // current_version_label so the dedup service can detect a cross-name
        // duplicate and treat the matched item as the canonical "existing"
        // item for MakeActive / IncrementVersion. The lookup itself is
        // covered by ix_catalog_versions_content_hash (partial index).
        cmd.CommandText = """
            SELECT cv.catalog_item_id AS itemId,
                   cv.version_label AS versionLabel,
                   ci.current_version_label AS currentLabel,
                   ci.name AS itemName,
                   ci.normalized_name AS itemNormalizedName
            FROM catalog_versions cv
            JOIN catalog_items ci ON cv.catalog_item_id = ci.id
            WHERE cv.content_hash = @hash
              AND cv.hash_format_version = @fmt
              AND ci.family_source = @source
            ORDER BY cv.published_at_utc DESC, cv.rowid DESC
            LIMIT 1
            """;
        cmd.Parameters.Add(new SqliteParameter("@hash", hexHash));
        cmd.Parameters.Add(new SqliteParameter("@fmt", hashFormatVersion));
        cmd.Parameters.Add(new SqliteParameter("@source", familySource));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var itemId = reader.GetString(reader.GetOrdinal("itemId"));
        var versionLabel = reader.GetString(reader.GetOrdinal("versionLabel"));
        var currentLabel = reader.IsDBNull(reader.GetOrdinal("currentLabel"))
            ? null
            : reader.GetString(reader.GetOrdinal("currentLabel"));
        var isCurrent = string.Equals(versionLabel, currentLabel, StringComparison.Ordinal);

        return new ContentHashMatch(
            CatalogItemId: itemId,
            MatchedVersionLabel: versionLabel,
            IsCurrentVersion: isCurrent,
            CurrentVersionLabel: currentLabel,
            MatchedItemName: reader.GetString(reader.GetOrdinal("itemName")),
            MatchedItemNormalizedName: reader.GetString(reader.GetOrdinal("itemNormalizedName")));
    }
}




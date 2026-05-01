using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalCatalogProvider : IFamilyCatalogProvider, IWritableFamilyCatalogProvider
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

    public async Task<FamilyCatalogItem> UpdateItemAsync(string id, string? name, string? description, string? categoryId, IReadOnlyList<string>? tags, ContentStatus? status, string? manufacturer = null, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
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

            if (manufacturer is not null)
            {
                setClauses.Add("manufacturer = @manufacturer");
                cmd.Parameters.Add(new SqliteParameter("@manufacturer", manufacturer));
            }

            setClauses.Add("updated_at_utc = @updatedAtUtc");
            cmd.Parameters.Add(new SqliteParameter("@updatedAtUtc", DateTimeOffset.UtcNow.ToString("o")));
            cmd.Parameters.Add(new SqliteParameter("@id", id));

            cmd.CommandText = $"UPDATE catalog_items SET {string.Join(", ", setClauses)} WHERE id = @id";
            await cmd.ExecuteNonQueryAsync(ct);

            if (tags is not null)
            {
                using var delCmd = connection.CreateCommand();
                delCmd.CommandText = "DELETE FROM catalog_tags WHERE catalog_item_id = @id";
                delCmd.Parameters.Add(new SqliteParameter("@id", id));
                await delCmd.ExecuteNonQueryAsync(ct);

                foreach (var tag in tags)
                {
                    var normalizedTag = Core.Services.FamilyManager.FamilySearchNormalizer.Normalize(tag);
                    using var tagCmd = connection.CreateCommand();
                    tagCmd.CommandText = "INSERT OR IGNORE INTO catalog_tags (catalog_item_id, tag, normalized_tag) VALUES (@id, @tag, @normalizedTag)";
                    tagCmd.Parameters.Add(new SqliteParameter("@id", id));
                    tagCmd.Parameters.Add(new SqliteParameter("@tag", tag));
                    tagCmd.Parameters.Add(new SqliteParameter("@normalizedTag", normalizedTag));
                    await tagCmd.ExecuteNonQueryAsync(ct);
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return (await GetItemAsync(id, ct))!;
    }

    public async Task<bool> DeleteItemAsync(string id, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        using var tx = connection.BeginTransaction();
        try
        {
            var relativePaths = new List<string>();
            using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.CommandText = "SELECT relative_path FROM family_files WHERE id IN (SELECT file_id FROM catalog_versions WHERE catalog_item_id = @id)";
                selectCmd.Parameters.Add(new SqliteParameter("@id", id));
                using var reader = await selectCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    relativePaths.Add(reader.GetString(0));
                }
            }

            using var delItem = connection.CreateCommand();
            delItem.CommandText = "DELETE FROM catalog_items WHERE id = @id";
            delItem.Parameters.Add(new SqliteParameter("@id", id));
            var rowsAffected = await delItem.ExecuteNonQueryAsync(ct);

            tx.Commit();

            if (rowsAffected > 0)
            {
                var dbRoot = _database.GetDatabaseRoot();
                var familyDir = Path.Combine(dbRoot, "files", id);
                if (Directory.Exists(familyDir))
                {
                    try
                    {
                        Directory.Delete(familyDir, recursive: true);
                    }
                    catch
                    {
                    }
                }
            }

            return rowsAffected > 0;
        }
        catch
        {
            tx.Rollback();
            throw;
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
            SELECT ci.* FROM catalog_items ci
            {whereSql}
            {orderBy}
            LIMIT @limit OFFSET @offset
            """;

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        foreach (var p in whereParams)
            cmd.Parameters.Add(p);
        cmd.Parameters.Add(limitOffsetParams[0]);
        cmd.Parameters.Add(limitOffsetParams[1]);

        var items = new List<FamilyCatalogItem>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = ReadCatalogItem(reader);
            items.Add(item);
        }

        if (items.Count > 0)
        {
            var tagsMap = await LoadAllTagsBatchAsync(connection, items.Select(i => i.Id).ToList(), ct);
            for (var i = 0; i < items.Count; i++)
            {
                tagsMap.TryGetValue(items[i].Id, out var tags);
                tags ??= [];
                items[i] = items[i] with { Tags = tags };
            }
        }

        return items;
    }

    public async Task<FamilyCatalogItem?> GetItemAsync(string id, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM catalog_items WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", id));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var item = ReadCatalogItem(reader);
        var tags = await LoadTagsAsync(connection, id, ct);
        return item with { Tags = tags };
    }

    public async Task<IReadOnlyList<FamilyCatalogVersion>> GetVersionsAsync(string catalogItemId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM catalog_versions WHERE catalog_item_id = @itemId ORDER BY published_at_utc DESC";
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));

        var versions = new List<FamilyCatalogVersion>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            versions.Add(ReadCatalogVersion(reader));
        }

        return versions;
    }

    public async Task<FamilyFileRecord?> GetFileAsync(string fileId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM family_files WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", fileId));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return ReadFileRecord(reader);
    }

    public async Task<int> GetItemCountAsync(CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM catalog_items";

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? (int)l : 0;
    }

    public async Task<IReadOnlyList<int>> GetAvailableRevitVersionsAsync(string catalogItemId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
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
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
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
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
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

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var itemId = reader.GetString(0);
            var tag = reader.GetString(1);
            result[itemId].Add(tag);
        }

        return result;
    }

    internal static FamilyCatalogItem ReadCatalogItem(SqliteDataReader reader)
    {
        var categoryPath = !reader.IsDBNull(reader.GetOrdinal("category_name"))
            ? reader.GetString(reader.GetOrdinal("category_name"))
            : null;

        var categoryId = TryGetString(reader, "category_id");

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
            ContentStatus: (ContentStatus)Enum.Parse(typeof(ContentStatus), reader.GetString(reader.GetOrdinal("content_status"))),
            CurrentVersionLabel: reader.IsDBNull(reader.GetOrdinal("current_version_label"))
                ? null
                : reader.GetString(reader.GetOrdinal("current_version_label")),
            Tags: [],
            PublishedBy: reader.IsDBNull(reader.GetOrdinal("published_by"))
                ? null
                : reader.GetString(reader.GetOrdinal("published_by")),
            CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))),
            UpdatedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at_utc"))));
    }

    private static FamilyCatalogVersion ReadCatalogVersion(SqliteDataReader reader) => new(
        Id: reader.GetString(reader.GetOrdinal("id")),
        CatalogItemId: reader.GetString(reader.GetOrdinal("catalog_item_id")),
        FileId: reader.GetString(reader.GetOrdinal("file_id")),
        VersionLabel: reader.GetString(reader.GetOrdinal("version_label")),
        Sha256: reader.GetString(reader.GetOrdinal("sha256")),
        RevitMajorVersion: reader.GetInt32(reader.GetOrdinal("revit_major_version")),
        TypesCount: reader.IsDBNull(reader.GetOrdinal("types_count"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("types_count")),
        ParametersCount: reader.IsDBNull(reader.GetOrdinal("parameters_count"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("parameters_count")),
        PublishedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("published_at_utc"))));

    private static FamilyFileRecord ReadFileRecord(SqliteDataReader reader) => new(
        Id: reader.GetString(reader.GetOrdinal("id")),
        RelativePath: reader.GetString(reader.GetOrdinal("relative_path")),
        FileName: reader.GetString(reader.GetOrdinal("file_name")),
        SizeBytes: reader.GetInt64(reader.GetOrdinal("size_bytes")),
        Sha256: reader.GetString(reader.GetOrdinal("sha256")),
        RevitMajorVersion: reader.GetInt32(reader.GetOrdinal("revit_major_version")),
        ImportedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("imported_at_utc"))));

    private static string? TryGetString(SqliteDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i) == columnName && !reader.IsDBNull(i))
                return reader.GetString(i);
        }
        return null;
    }
}

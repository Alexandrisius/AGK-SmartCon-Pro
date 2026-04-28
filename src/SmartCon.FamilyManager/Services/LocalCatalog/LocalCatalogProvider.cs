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

    public async Task<FamilyImportResult> ImportAsync(FamilyImportRequest request, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public async Task<FamilyBatchImportResult> ImportFolderAsync(FamilyFolderImportRequest request, IProgress<FamilyImportProgress>? progress, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    public async Task<FamilyCatalogItem> UpdateItemAsync(string id, string? name, string? description, string? category, IReadOnlyList<string>? tags, FamilyContentStatus? status, CancellationToken ct = default)
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

            if (category is not null)
            {
                setClauses.Add("category_name = @categoryName");
                cmd.Parameters.Add(new SqliteParameter("@categoryName", category));
            }

            if (status is not null)
            {
                setClauses.Add("status = @status");
                cmd.Parameters.Add(new SqliteParameter("@status", status.Value.ToString()));
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
        throw new NotImplementedException();
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

        for (var i = 0; i < items.Count; i++)
        {
            var tags = await LoadTagsAsync(connection, items[i].Id, ct);
            items[i] = items[i] with { Tags = tags };
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
        cmd.CommandText = "SELECT * FROM catalog_versions WHERE catalog_item_id = @itemId ORDER BY imported_at_utc DESC";
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

    internal static FamilyCatalogItem ReadCatalogItem(SqliteDataReader reader) => new(
        Id: reader.GetString(reader.GetOrdinal("id")),
        ProviderId: reader.GetString(reader.GetOrdinal("provider_id")),
        Name: reader.GetString(reader.GetOrdinal("name")),
        NormalizedName: reader.GetString(reader.GetOrdinal("normalized_name")),
        Description: reader.IsDBNull(reader.GetOrdinal("description"))
            ? null
            : reader.GetString(reader.GetOrdinal("description")),
        CategoryName: reader.IsDBNull(reader.GetOrdinal("category_name"))
            ? null
            : reader.GetString(reader.GetOrdinal("category_name")),
        Manufacturer: reader.IsDBNull(reader.GetOrdinal("manufacturer"))
            ? null
            : reader.GetString(reader.GetOrdinal("manufacturer")),
        Status: (FamilyContentStatus)Enum.Parse(typeof(FamilyContentStatus), reader.GetString(reader.GetOrdinal("status"))),
        CurrentVersionId: reader.IsDBNull(reader.GetOrdinal("current_version_id"))
            ? null
            : reader.GetString(reader.GetOrdinal("current_version_id")),
        Tags: [],
        CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))),
        UpdatedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at_utc"))));

    private static FamilyCatalogVersion ReadCatalogVersion(SqliteDataReader reader) => new(
        Id: reader.GetString(reader.GetOrdinal("id")),
        CatalogItemId: reader.GetString(reader.GetOrdinal("catalog_item_id")),
        FileId: reader.GetString(reader.GetOrdinal("file_id")),
        VersionLabel: reader.GetString(reader.GetOrdinal("version_label")),
        Sha256: reader.GetString(reader.GetOrdinal("sha256")),
        RevitMajorVersion: reader.IsDBNull(reader.GetOrdinal("revit_major_version"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("revit_major_version")),
        TypesCount: reader.IsDBNull(reader.GetOrdinal("types_count"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("types_count")),
        ParametersCount: reader.IsDBNull(reader.GetOrdinal("parameters_count"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("parameters_count")),
        ImportedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("imported_at_utc"))));

    internal static FamilyFileRecord ReadFileRecord(SqliteDataReader reader) => new(
        Id: reader.GetString(reader.GetOrdinal("id")),
        OriginalPath: reader.IsDBNull(reader.GetOrdinal("original_path"))
            ? null
            : reader.GetString(reader.GetOrdinal("original_path")),
        CachedPath: reader.IsDBNull(reader.GetOrdinal("cached_path"))
            ? null
            : reader.GetString(reader.GetOrdinal("cached_path")),
        FileName: reader.GetString(reader.GetOrdinal("file_name")),
        SizeBytes: reader.GetInt64(reader.GetOrdinal("size_bytes")),
        Sha256: reader.GetString(reader.GetOrdinal("sha256")),
        LastWriteTimeUtc: reader.IsDBNull(reader.GetOrdinal("last_write_time_utc"))
            ? null
            : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("last_write_time_utc"))),
        StorageMode: (FamilyFileStorageMode)Enum.Parse(typeof(FamilyFileStorageMode), reader.GetString(reader.GetOrdinal("storage_mode"))));
}

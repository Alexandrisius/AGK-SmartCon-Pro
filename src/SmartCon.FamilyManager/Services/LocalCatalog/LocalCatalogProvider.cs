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
                // #187: tags enrichment must never rebuild the record
                // field-by-field — a rebuild dropped RevitCategoryId once
                // (SearchAsync always returned null, silently breaking
                // presence badges and the batch stale check). The `with`
                // expression carries every field, present and future.
                items[i] = items[i] with { Tags = tags };
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
        return item with { Tags = tags };
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
        var revitCategoryId = TryGetInt(reader, "revit_category_id");
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
            MinRevitMajorVersion: TryGetInt(reader, "min_revit_major_version"),
            RevitCategoryId: revitCategoryId);
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

}




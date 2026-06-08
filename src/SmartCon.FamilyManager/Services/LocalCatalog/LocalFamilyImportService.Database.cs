using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed partial class LocalFamilyImportService
{
    private async Task<FamilyCatalogItem?> FindByNameAsync(string name, CancellationToken ct)
    {
        var normalizedName = FamilyNameNormalizer.Normalize(name);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM catalog_items WHERE normalized_name = @name LIMIT 1";
        cmd.Parameters.Add(new SqliteParameter("@name", normalizedName));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct))
            return null;

        return LocalCatalogProvider.ReadCatalogItem(reader) with { Tags = [] };
    }

    private async Task<FamilyCatalogVersion?> FindVersionByHashAndRevitAsync(string catalogItemId, string sha256, int revitVersion, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT cv.* FROM catalog_versions cv
            INNER JOIN family_files ff ON ff.id = cv.file_id
            WHERE cv.catalog_item_id = @itemId AND ff.sha256 = @sha256 AND cv.revit_major_version = @revitVersion
            LIMIT 1
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@sha256", sha256));
        cmd.Parameters.Add(new SqliteParameter("@revitVersion", revitVersion));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct))
            return null;

        return new FamilyCatalogVersion(
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
    }

    /// <summary>
    /// Checks whether the given SHA256 matches the current (active) version of the specified catalog item.
    /// Used by UpdateFamilyAsync to prevent re-uploading the same file that is already current.
    /// </summary>
    private async Task<FamilyCatalogVersion?> FindCurrentVersionByHashAsync(string catalogItemId, string sha256, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT cv.* FROM catalog_versions cv
            INNER JOIN catalog_items ci ON ci.id = cv.catalog_item_id AND ci.current_version_label = cv.version_label
            INNER JOIN family_files ff ON ff.id = cv.file_id
            WHERE cv.catalog_item_id = @itemId AND ff.sha256 = @sha256
            LIMIT 1
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@sha256", sha256));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct))
            return null;

        return new FamilyCatalogVersion(
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
    }

    private async Task<string> GetNextVersionLabelAsync(string catalogItemId, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version_label FROM catalog_versions WHERE catalog_item_id = @itemId ORDER BY published_at_utc DESC LIMIT 1";
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is string label && label.StartsWith("v") && int.TryParse(label[1..], out var num))
        {
            return $"v{num + 1}";
        }

        return "v2";
    }

    private static async Task InsertFileRecordAsync(SqliteConnection connection, string id,
        string relativePath, FamilyMetadataExtractionResult metadata,
        int revitVersion, DateTimeOffset now, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO family_files (id, relative_path, file_name, size_bytes, sha256, revit_major_version, imported_at_utc)
            VALUES (@id, @relativePath, @fileName, @sizeBytes, @sha256, @revitVersion, @importedAtUtc)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@relativePath", relativePath));
        cmd.Parameters.Add(new SqliteParameter("@fileName", metadata.FileName));
        cmd.Parameters.Add(new SqliteParameter("@sizeBytes", metadata.FileSizeBytes));
        cmd.Parameters.Add(new SqliteParameter("@sha256", metadata.Sha256));
        cmd.Parameters.Add(new SqliteParameter("@revitVersion", revitVersion));
        cmd.Parameters.Add(new SqliteParameter("@importedAtUtc", now.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertCatalogItemAsync(SqliteConnection connection, string id,
        string displayName, string normalizedName, FamilyImportRequest request, DateTimeOffset now,
        string versionLabel, CancellationToken ct)
    {
        displayName = displayName.Trim();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_items (id, name, normalized_name, description, category_name, category_id, manufacturer, content_status, current_version_label, published_by, family_source, revit_category, created_at_utc, updated_at_utc)
            VALUES (@id, @name, @normalizedName, @description, @categoryName, @categoryId, @manufacturer, @status, @versionLabel, @publishedBy, @familySource, @revitCategory, @createdAtUtc, @updatedAtUtc)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@name", displayName));
        cmd.Parameters.Add(new SqliteParameter("@normalizedName", normalizedName));
        cmd.Parameters.Add(new SqliteParameter("@description", request.Description ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@categoryName", request.Category ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@categoryId", request.CategoryId ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@manufacturer", DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@status", ContentStatus.Active.ToString()));
        cmd.Parameters.Add(new SqliteParameter("@versionLabel", versionLabel));
        cmd.Parameters.Add(new SqliteParameter("@publishedBy", DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@familySource", request.FamilySource));
        cmd.Parameters.Add(new SqliteParameter("@revitCategory", request.RevitCategory ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@createdAtUtc", now.ToString("o")));
        cmd.Parameters.Add(new SqliteParameter("@updatedAtUtc", now.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpdateCatalogItemWithNameAsync(SqliteConnection connection, string id,
        string newName, string normalizedName, string versionLabel, DateTimeOffset now, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE catalog_items
            SET name = @name, normalized_name = @normalizedName, current_version_label = @versionLabel, updated_at_utc = @updatedAtUtc
            WHERE id = @id
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@name", newName));
        cmd.Parameters.Add(new SqliteParameter("@normalizedName", normalizedName));
        cmd.Parameters.Add(new SqliteParameter("@versionLabel", versionLabel));
        cmd.Parameters.Add(new SqliteParameter("@updatedAtUtc", now.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpdateCatalogItemCategoryAsync(SqliteConnection connection, string id,
        string? categoryId, string? categoryName, DateTimeOffset now, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE catalog_items
            SET category_id = @categoryId, category_name = @categoryName, updated_at_utc = @updatedAtUtc
            WHERE id = @id
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@categoryId", categoryId ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@categoryName", categoryName ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@updatedAtUtc", now.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpdateCatalogItemVersionAsync(SqliteConnection connection, string id,
        string versionLabel, DateTimeOffset now, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE catalog_items SET current_version_label = @versionLabel, updated_at_utc = @updatedAtUtc WHERE id = @id
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@versionLabel", versionLabel));
        cmd.Parameters.Add(new SqliteParameter("@updatedAtUtc", now.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertVersionAsync(SqliteConnection connection, string versionId,
        string catalogItemId, string fileId, string versionLabel,
        FamilyMetadataExtractionResult metadata, int revitVersion,
        DateTimeOffset now, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, sha256, revit_major_version, types_count, parameters_count, published_at_utc)
            VALUES (@id, @catalogItemId, @fileId, @versionLabel, @sha256, @revitMajorVersion, @typesCount, @parametersCount, @publishedAtUtc)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", versionId));
        cmd.Parameters.Add(new SqliteParameter("@catalogItemId", catalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
        cmd.Parameters.Add(new SqliteParameter("@versionLabel", versionLabel));
        cmd.Parameters.Add(new SqliteParameter("@sha256", metadata.Sha256));
        cmd.Parameters.Add(new SqliteParameter("@revitMajorVersion", revitVersion));
        cmd.Parameters.Add(new SqliteParameter("@typesCount",
            metadata.Types is not null ? (object)metadata.Types.Count : DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@parametersCount",
            metadata.Parameters is not null ? (object)metadata.Parameters.Count : DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@publishedAtUtc", now.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertTagAsync(SqliteConnection connection, string catalogItemId, string tag, CancellationToken ct)
    {
        var normalizedTag = FamilySearchNormalizer.Normalize(tag);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO catalog_tags (catalog_item_id, tag, normalized_tag)
            VALUES (@catalogItemId, @tag, @normalizedTag)
            """;
        cmd.Parameters.Add(new SqliteParameter("@catalogItemId", catalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@tag", tag));
        cmd.Parameters.Add(new SqliteParameter("@normalizedTag", normalizedTag));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the current version for a catalog item (by current_version_label).
    /// </summary>
    private async Task<FamilyCatalogVersion?> FindCurrentVersionAsync(string catalogItemId, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT cv.* FROM catalog_versions cv
            INNER JOIN catalog_items ci ON ci.id = cv.catalog_item_id AND ci.current_version_label = cv.version_label
            WHERE cv.catalog_item_id = @itemId
            LIMIT 1
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct))
            return null;

        return new FamilyCatalogVersion(
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
    }

    /// <summary>
    /// Overwrites the file for the current version without changing current_version_label.
    /// Updates family_files.sha256 and size_bytes.
    /// </summary>
    private async Task<FamilyImportResult> OverwriteCurrentAsync(FamilyBatchImportItem item, CancellationToken ct)
    {
        var currentVersion = await FindCurrentVersionAsync(item.ExistingCatalogItemId!, ct);
        if (currentVersion is null)
        {
            return new FamilyImportResult(
                Success: false,
                CatalogItemId: item.ExistingCatalogItemId,
                VersionId: null,
                FileId: null,
                FileName: item.FileName,
                VersionLabel: null,
                ErrorMessage: "Current version not found");
        }

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();

        // Get current file path
        using var pathCmd = connection.CreateCommand();
        pathCmd.CommandText = "SELECT relative_path FROM family_files WHERE id = @fileId";
        pathCmd.Parameters.Add(new SqliteParameter("@fileId", currentVersion.FileId));
        var relativePath = await pathCmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrEmpty(relativePath))
        {
            tx.Rollback();
            return new FamilyImportResult(
                Success: false,
                CatalogItemId: item.ExistingCatalogItemId,
                VersionId: null,
                FileId: null,
                FileName: item.FileName,
                VersionLabel: null,
                ErrorMessage: "Current file path not found");
        }

        var absolutePath = Path.Combine(_database.GetDatabaseRoot(), relativePath);
        var metadata = await _metadataService.ExtractAsync(item.FilePath, ct);

        try
        {
            // Copy file over existing
            await Task.Run(() =>
            {
                var existingAttributes = File.GetAttributes(absolutePath);
                var wasReadOnly = (existingAttributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
                try
                {
                    if (wasReadOnly)
                    {
                        File.SetAttributes(absolutePath, existingAttributes & ~FileAttributes.ReadOnly);
                    }
                    File.Copy(item.FilePath, absolutePath, overwrite: true);
                }
                finally
                {
                    var currentAttributes = File.GetAttributes(absolutePath);
                    if (wasReadOnly)
                    {
                        File.SetAttributes(absolutePath, currentAttributes | FileAttributes.ReadOnly);
                    }
                    else
                    {
                        File.SetAttributes(absolutePath, currentAttributes & ~FileAttributes.ReadOnly);
                    }
                }
            }, ct);

            // Update family_files
            using var updateFileCmd = connection.CreateCommand();
            updateFileCmd.CommandText = """
                UPDATE family_files
                SET sha256 = @sha256, size_bytes = @sizeBytes, file_name = @fileName, imported_at_utc = @importedAtUtc
                WHERE id = @fileId
                """;
            updateFileCmd.Parameters.Add(new SqliteParameter("@fileId", currentVersion.FileId));
            updateFileCmd.Parameters.Add(new SqliteParameter("@sha256", metadata.Sha256));
            updateFileCmd.Parameters.Add(new SqliteParameter("@sizeBytes", metadata.FileSizeBytes));
            updateFileCmd.Parameters.Add(new SqliteParameter("@fileName", metadata.FileName));
            updateFileCmd.Parameters.Add(new SqliteParameter("@importedAtUtc", DateTimeOffset.UtcNow.ToString("o")));
            await updateFileCmd.ExecuteNonQueryAsync(ct);

            // Update catalog_items.updated_at_utc and optionally category_id + category_name
            if (!string.IsNullOrEmpty(item.TargetCategoryId))
            {
                await UpdateCatalogItemCategoryAsync(connection, item.ExistingCatalogItemId!, item.TargetCategoryId, item.TargetCategoryName, DateTimeOffset.UtcNow, ct);
            }
            else
            {
                using var updateItemCmd = connection.CreateCommand();
                updateItemCmd.CommandText = "UPDATE catalog_items SET updated_at_utc = @updatedAtUtc WHERE id = @itemId";
                updateItemCmd.Parameters.Add(new SqliteParameter("@itemId", item.ExistingCatalogItemId));
                updateItemCmd.Parameters.Add(new SqliteParameter("@updatedAtUtc", DateTimeOffset.UtcNow.ToString("o")));
                await updateItemCmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        await ImportTypeCatalogIfPresentAsync(item.FilePath, item.OriginalSourcePath, item.ExistingCatalogItemId!, currentVersion.Id, currentVersion.VersionLabel, ct);

        return new FamilyImportResult(
            Success: true,
            CatalogItemId: item.ExistingCatalogItemId,
            VersionId: currentVersion.Id,
            FileId: currentVersion.FileId,
            FileName: metadata.FileName,
            VersionLabel: currentVersion.VersionLabel,
            ErrorMessage: null,
            WasNewVersion: false);
    }
}

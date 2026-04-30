using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalFamilyImportService : IFamilyImportService
{
    private readonly LocalCatalogDatabase _database;
    private readonly LocalCatalogMigrator _migrator;
    private readonly LocalCatalogProvider _catalogProvider;
    private readonly StoragePathResolver _pathResolver;
    private readonly IFamilyMetadataExtractionService _metadataService;

    public LocalFamilyImportService(
        LocalCatalogDatabase database,
        LocalCatalogMigrator migrator,
        LocalCatalogProvider catalogProvider,
        StoragePathResolver pathResolver,
        IFamilyMetadataExtractionService metadataService)
    {
        _database = database;
        _migrator = migrator;
        _catalogProvider = catalogProvider;
        _pathResolver = pathResolver;
        _metadataService = metadataService;
    }

    public async Task<FamilyImportResult> ImportFileAsync(FamilyImportRequest request, CancellationToken ct = default)
    {
        await _migrator.MigrateAsync(ct);

        var filePath = request.FilePath;
        if (!File.Exists(filePath))
        {
            return new FamilyImportResult(
                Success: false,
                CatalogItemId: null,
                VersionId: null,
                FileId: null,
                FileName: Path.GetFileName(filePath),
                VersionLabel: null,
                ErrorMessage: $"File not found: {filePath}");
        }

        var metadata = await _metadataService.ExtractAsync(filePath, ct);
        var sha256 = metadata.Sha256;
        var revitVersion = request.RevitMajorVersion;

        SmartConLogger.Info($"[Import] File: {Path.GetFileName(filePath)}, SHA256: {sha256[..16]}..., Revit: R{revitVersion}");

        var existingItem = await FindByNameAsync(Path.GetFileNameWithoutExtension(filePath), ct);
        if (existingItem is not null)
        {
            var existingVersion = await FindVersionByHashAndRevitAsync(existingItem.Id, sha256, revitVersion, ct);
            if (existingVersion is not null)
            {
                return new FamilyImportResult(
                    Success: true,
                    CatalogItemId: existingItem.Id,
                    VersionId: existingVersion.Id,
                    FileId: existingVersion.FileId,
                    FileName: metadata.FileName,
                    VersionLabel: existingVersion.VersionLabel,
                    ErrorMessage: null,
                    WasSkippedAsDuplicate: true);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var fileRecordId = Guid.NewGuid().ToString();
        var catalogItemId = existingItem?.Id ?? Guid.NewGuid().ToString();
        var versionId = Guid.NewGuid().ToString();
        var normalizedName = FamilyNameNormalizer.Normalize(Path.GetFileNameWithoutExtension(filePath));

        var versionLabel = existingItem is not null
            ? await GetNextVersionLabelAsync(existingItem.Id, ct)
            : "v1";

        string relativePath;
        try
        {
            _pathResolver.EnsureFamilyDirectories(catalogItemId, versionLabel, revitVersion);
            var fileName = metadata.FileName;
            var absolutePath = _pathResolver.GetRfaFilePath(catalogItemId, versionLabel, revitVersion, fileName);
            File.Copy(filePath, absolutePath, overwrite: true);
            relativePath = _pathResolver.GetRelativePath(absolutePath);
            SmartConLogger.Info($"[Import] Copied to managed storage: {absolutePath}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"[Import] Copy FAILED: {ex.Message}");
            return new FamilyImportResult(
                Success: false,
                CatalogItemId: null,
                VersionId: null,
                FileId: null,
                FileName: metadata.FileName,
                VersionLabel: null,
                ErrorMessage: $"Failed to copy file to managed storage: {ex.Message}");
        }

        try
        {
            using var connection = _database.CreateConnection();
            await connection.OpenAsync(ct);
            using var tx = connection.BeginTransaction();

            using (var fkCmd = connection.CreateCommand())
            {
                fkCmd.CommandText = "PRAGMA foreign_keys=ON;";
                await fkCmd.ExecuteNonQueryAsync(ct);
            }

            try
            {
                await InsertFileRecordAsync(connection, fileRecordId, relativePath, metadata, revitVersion, now, ct);

                if (existingItem is null)
                {
                    await InsertCatalogItemAsync(connection, catalogItemId, normalizedName, request, now, versionLabel, ct);
                }
                else
                {
                    await UpdateCatalogItemVersionAsync(connection, catalogItemId, versionLabel, now, ct);
                }

                await InsertVersionAsync(connection, versionId, catalogItemId, fileRecordId, versionLabel, metadata, revitVersion, now, ct);

                if (existingItem is null && request.Tags is not null)
                {
                    foreach (var tag in request.Tags)
                    {
                        await InsertTagAsync(connection, catalogItemId, tag, ct);
                    }
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            _database.Checkpoint();

            return new FamilyImportResult(
                Success: true,
                CatalogItemId: catalogItemId,
                VersionId: versionId,
                FileId: fileRecordId,
                FileName: metadata.FileName,
                VersionLabel: versionLabel,
                ErrorMessage: null,
                WasNewVersion: existingItem is not null);
        }
        catch
        {
            try
            {
                var absPath = Path.Combine(_database.GetDatabaseRoot(), relativePath);
                if (File.Exists(absPath))
                    File.Delete(absPath);
            }
            catch
            {
            }

            throw;
        }
    }

    public async Task<FamilyBatchImportResult> ImportFolderAsync(FamilyFolderImportRequest request, IProgress<FamilyImportProgress>? progress, CancellationToken ct = default)
    {
        var searchOption = request.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.GetFiles(request.FolderPath, "*.rfa", searchOption);

        var results = new List<FamilyImportResult>();
        var successCount = 0;
        var skippedCount = 0;
        var errorCount = 0;

        for (var i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            var file = files[i];
            var fileName = Path.GetFileName(file);

            progress?.Report(new FamilyImportProgress(
                CurrentFileIndex: i,
                TotalFiles: files.Length,
                CurrentFileName: fileName,
                SuccessCount: successCount,
                SkippedCount: skippedCount,
                ErrorCount: errorCount));

            try
            {
                var importRequest = new FamilyImportRequest(
                    FilePath: file,
                    RevitMajorVersion: request.RevitMajorVersion,
                    Category: request.Category,
                    Tags: request.Tags,
                    Description: request.Description);

                var result = await ImportFileAsync(importRequest, ct);
                results.Add(result);

                if (result.WasSkippedAsDuplicate)
                    skippedCount++;
                else if (result.Success)
                    successCount++;
                else
                    errorCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errorCount++;
                results.Add(new FamilyImportResult(
                    Success: false,
                    CatalogItemId: null,
                    VersionId: null,
                    FileId: null,
                    FileName: fileName,
                    VersionLabel: null,
                    ErrorMessage: ex.Message));
            }
        }

        progress?.Report(new FamilyImportProgress(
            CurrentFileIndex: files.Length - 1,
            TotalFiles: files.Length,
            CurrentFileName: "",
            SuccessCount: successCount,
            SkippedCount: skippedCount,
            ErrorCount: errorCount));

        return new FamilyBatchImportResult(
            Results: results,
            TotalFiles: files.Length,
            SuccessCount: successCount,
            SkippedCount: skippedCount,
            ErrorCount: errorCount);
    }

    private async Task<FamilyCatalogItem?> FindByNameAsync(string name, CancellationToken ct)
    {
        var normalizedName = FamilyNameNormalizer.Normalize(name);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM catalog_items WHERE normalized_name = @name LIMIT 1";
        cmd.Parameters.Add(new SqliteParameter("@name", normalizedName));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return LocalCatalogProvider.ReadCatalogItem(reader) with { Tags = [] };
    }

    private async Task<FamilyCatalogVersion?> FindVersionByHashAndRevitAsync(string catalogItemId, string sha256, int revitVersion, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
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

        using var reader = await cmd.ExecuteReaderAsync(ct);
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
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version_label FROM catalog_versions WHERE catalog_item_id = @itemId ORDER BY published_at_utc DESC LIMIT 1";
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));

        var result = await cmd.ExecuteScalarAsync(ct);
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
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertCatalogItemAsync(SqliteConnection connection, string id,
        string normalizedName, FamilyImportRequest request, DateTimeOffset now,
        string versionLabel, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_items (id, name, normalized_name, description, category_name, manufacturer, content_status, current_version_label, published_by, created_at_utc, updated_at_utc)
            VALUES (@id, @name, @normalizedName, @description, @categoryName, @manufacturer, @status, @versionLabel, @publishedBy, @createdAtUtc, @updatedAtUtc)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@name", Path.GetFileNameWithoutExtension(request.FilePath)));
        cmd.Parameters.Add(new SqliteParameter("@normalizedName", normalizedName));
        cmd.Parameters.Add(new SqliteParameter("@description", request.Description ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@categoryName", request.Category ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@manufacturer", DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@status", ContentStatus.Active.ToString()));
        cmd.Parameters.Add(new SqliteParameter("@versionLabel", versionLabel));
        cmd.Parameters.Add(new SqliteParameter("@publishedBy", DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@createdAtUtc", now.ToString("o")));
        cmd.Parameters.Add(new SqliteParameter("@updatedAtUtc", now.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct);
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
        await cmd.ExecuteNonQueryAsync(ct);
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
        await cmd.ExecuteNonQueryAsync(ct);
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
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

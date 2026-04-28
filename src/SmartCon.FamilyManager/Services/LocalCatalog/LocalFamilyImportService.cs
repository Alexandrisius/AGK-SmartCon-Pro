using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalFamilyImportService : IFamilyImportService
{
    private readonly LocalCatalogDatabase _database;
    private readonly LocalCatalogMigrator _migrator;
    private readonly LocalCatalogProvider _catalogProvider;
    private readonly Sha256FileHasher _hasher;
    private readonly IFamilyMetadataExtractionService _metadataService;

    public LocalFamilyImportService(
        LocalCatalogDatabase database,
        LocalCatalogMigrator migrator,
        LocalCatalogProvider catalogProvider,
        Sha256FileHasher hasher,
        IFamilyMetadataExtractionService metadataService)
    {
        _database = database;
        _migrator = migrator;
        _catalogProvider = catalogProvider;
        _hasher = hasher;
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
                ErrorMessage: $"File not found: {filePath}");
        }

        var metadata = await _metadataService.ExtractAsync(filePath, ct);
        var sha256 = metadata.Sha256;

        var duplicateItem = await FindByHashAsync(sha256, ct);
        if (duplicateItem is not null)
        {
            return new FamilyImportResult(
                Success: true,
                CatalogItemId: duplicateItem.Id,
                VersionId: duplicateItem.CurrentVersionId,
                FileId: null,
                FileName: metadata.FileName,
                ErrorMessage: null,
                WasSkippedAsDuplicate: true,
                DuplicateCatalogItemId: duplicateItem.Id);
        }

        var now = DateTimeOffset.UtcNow;
        var fileRecordId = Guid.NewGuid().ToString();
        var catalogItemId = Guid.NewGuid().ToString();
        var versionId = Guid.NewGuid().ToString();
        var normalizedName = FamilyNameNormalizer.Normalize(Path.GetFileNameWithoutExtension(filePath));

        string? relativeCachedPath = null;
        var canonicalRoot = GetCanonicalRoot();
        var cacheCopied = false;

        if (request.StorageMode == FamilyFileStorageMode.Cached)
        {
            try
            {
                relativeCachedPath = $"cache/rfa/{sha256[..2]}/{sha256}.rfa";
                var absoluteCachePath = Path.Combine(canonicalRoot, relativeCachedPath);
                var cacheDir = Path.GetDirectoryName(absoluteCachePath)!;
                Directory.CreateDirectory(cacheDir);
                File.Copy(filePath, absoluteCachePath, overwrite: true);
                cacheCopied = true;
            }
            catch
            {
                relativeCachedPath = null;
            }
        }

        try
        {
            using var connection = _database.CreateConnection();
            await connection.OpenAsync(ct);
            using var tx = connection.BeginTransaction();

            // Ensure DELETE journal mode for this connection
            using (var journalCmd = connection.CreateCommand())
            {
                journalCmd.CommandText = "PRAGMA journal_mode=DELETE;";
                await journalCmd.ExecuteNonQueryAsync(ct);
            }

            try
            {
                await InsertFileRecordAsync(connection, fileRecordId, filePath, relativeCachedPath,
                    metadata, request.StorageMode == FamilyFileStorageMode.Cached && !cacheCopied
                        ? FamilyFileStorageMode.Missing
                        : request.StorageMode, ct);

                await InsertCatalogItemAsync(connection, catalogItemId, normalizedName, request, now, versionId, ct);

                await InsertVersionAsync(connection, versionId, catalogItemId, fileRecordId, metadata, now, ct);

                if (request.Tags is not null)
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

            // Force checkpoint to ensure data is written to .db file immediately
            _database.Checkpoint();

            return new FamilyImportResult(
                Success: true,
                CatalogItemId: catalogItemId,
                VersionId: versionId,
                FileId: fileRecordId,
                FileName: metadata.FileName,
                ErrorMessage: null);
        }
        catch
        {
            if (cacheCopied && relativeCachedPath is not null)
            {
                try
                {
                    var absPath = Path.Combine(canonicalRoot, relativeCachedPath);
                    if (File.Exists(absPath))
                        File.Delete(absPath);
                }
                catch
                {
                    // best-effort
                }
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
                    Category: request.Category,
                    Tags: request.Tags,
                    Description: request.Description,
                    StorageMode: request.StorageMode);

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

    private async Task<FamilyCatalogItem?> FindByHashAsync(string sha256, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ci.* FROM catalog_items ci
            INNER JOIN catalog_versions cv ON cv.catalog_item_id = ci.id
            INNER JOIN family_files ff ON ff.id = cv.file_id
            WHERE ff.sha256 = @sha256
            LIMIT 1
            """;
        cmd.Parameters.Add(new SqliteParameter("@sha256", sha256));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var item = LocalCatalogProvider.ReadCatalogItem(reader);
        return item with { Tags = [] };
    }

    private static async Task InsertFileRecordAsync(SqliteConnection connection, string id,
        string originalPath, string? cachedPath, FamilyMetadataExtractionResult metadata,
        FamilyFileStorageMode storageMode, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO family_files (id, original_path, cached_path, file_name, size_bytes, sha256, last_write_time_utc, storage_mode)
            VALUES (@id, @originalPath, @cachedPath, @fileName, @sizeBytes, @sha256, @lastWriteTimeUtc, @storageMode)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@originalPath", originalPath ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@cachedPath", cachedPath ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@fileName", metadata.FileName));
        cmd.Parameters.Add(new SqliteParameter("@sizeBytes", metadata.FileSizeBytes));
        cmd.Parameters.Add(new SqliteParameter("@sha256", metadata.Sha256));
        cmd.Parameters.Add(new SqliteParameter("@lastWriteTimeUtc",
            metadata.LastWriteTimeUtc.HasValue ? metadata.LastWriteTimeUtc.Value.ToString("o") : (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@storageMode", storageMode.ToString()));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertCatalogItemAsync(SqliteConnection connection, string id,
        string normalizedName, FamilyImportRequest request, DateTimeOffset now,
        string versionId, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_items (id, provider_id, name, normalized_name, description, category_name, manufacturer, status, current_version_id, created_at_utc, updated_at_utc)
            VALUES (@id, 'local', @name, @normalizedName, @description, @categoryName, @manufacturer, @status, @currentVersionId, @createdAtUtc, @updatedAtUtc)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@name", Path.GetFileNameWithoutExtension(request.FilePath)));
        cmd.Parameters.Add(new SqliteParameter("@normalizedName", normalizedName));
        cmd.Parameters.Add(new SqliteParameter("@description", request.Description ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@categoryName", request.Category ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@manufacturer", DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@status", FamilyContentStatus.Draft.ToString()));
        cmd.Parameters.Add(new SqliteParameter("@currentVersionId", versionId));
        cmd.Parameters.Add(new SqliteParameter("@createdAtUtc", now.ToString("o")));
        cmd.Parameters.Add(new SqliteParameter("@updatedAtUtc", now.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertVersionAsync(SqliteConnection connection, string versionId,
        string catalogItemId, string fileId, FamilyMetadataExtractionResult metadata,
        DateTimeOffset now, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, sha256, revit_major_version, types_count, parameters_count, imported_at_utc)
            VALUES (@id, @catalogItemId, @fileId, @versionLabel, @sha256, @revitMajorVersion, @typesCount, @parametersCount, @importedAtUtc)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", versionId));
        cmd.Parameters.Add(new SqliteParameter("@catalogItemId", catalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
        cmd.Parameters.Add(new SqliteParameter("@versionLabel", "v1"));
        cmd.Parameters.Add(new SqliteParameter("@sha256", metadata.Sha256));
        cmd.Parameters.Add(new SqliteParameter("@revitMajorVersion",
            metadata.RevitMajorVersion.HasValue ? (object)metadata.RevitMajorVersion.Value : DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@typesCount",
            metadata.Types is not null ? (object)metadata.Types.Count : DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@parametersCount",
            metadata.Parameters is not null ? (object)metadata.Parameters.Count : DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@importedAtUtc", now.ToString("o")));
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

    private static string GetCanonicalRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "SmartCon", "FamilyManager");
    }
}

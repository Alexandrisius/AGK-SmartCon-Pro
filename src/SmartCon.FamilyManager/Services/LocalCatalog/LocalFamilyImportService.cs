using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed partial class LocalFamilyImportService : IFamilyImportService
{
    private readonly LocalCatalogDatabase _database;
    private readonly LocalCatalogMigrator _migrator;
    private readonly LocalCatalogProvider _catalogProvider;
    private readonly StoragePathResolver _pathResolver;
    private readonly IFamilyMetadataExtractionService _metadataService;
    private readonly IRevitFileInfoReader? _fileInfoReader;

    public LocalFamilyImportService(
        LocalCatalogDatabase database,
        LocalCatalogMigrator migrator,
        LocalCatalogProvider catalogProvider,
        StoragePathResolver pathResolver,
        IFamilyMetadataExtractionService metadataService,
        IRevitFileInfoReader? fileInfoReader = null)
    {
        _database = database;
        _migrator = migrator;
        _catalogProvider = catalogProvider;
        _pathResolver = pathResolver;
        _metadataService = metadataService;
        _fileInfoReader = fileInfoReader;
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
        var revitVersion = _fileInfoReader?.ReadRevitVersion(filePath) ?? request.RevitMajorVersion;

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

        var copyResult = await CopyToManagedStorageAsync(filePath, catalogItemId, versionLabel, revitVersion, metadata);
        if (!copyResult.Success)
            return new FamilyImportResult(
                Success: false,
                CatalogItemId: null,
                VersionId: null,
                FileId: null,
                FileName: metadata.FileName,
                VersionLabel: null,
                ErrorMessage: copyResult.ErrorMessage);

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
                await InsertFileRecordAsync(connection, fileRecordId, copyResult.RelativePath!, metadata, revitVersion, now, ct);

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
            await CleanupFileAsync(copyResult.RelativePath);
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
                var detectedVersion = _fileInfoReader?.ReadRevitVersion(file) ?? request.RevitMajorVersion;
                var importRequest = new FamilyImportRequest(
                    FilePath: file,
                    RevitMajorVersion: detectedVersion,
                    Category: request.Category,
                    Tags: request.Tags,
                    Description: request.Description,
                    CategoryId: request.CategoryId);

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

    public async Task<FamilyImportResult> UpdateFamilyAsync(FamilyUpdateRequest request, CancellationToken ct = default)
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
        var revitVersion = _fileInfoReader?.ReadRevitVersion(filePath) ?? request.RevitMajorVersion;

        SmartConLogger.Info($"[Update] File: {Path.GetFileName(filePath)}, SHA256: {sha256[..16]}..., Revit: R{revitVersion}, TargetItem: {request.CatalogItemId}");

        var existingVersion = await FindVersionByHashAndRevitAsync(request.CatalogItemId, sha256, revitVersion, ct);
        if (existingVersion is not null)
        {
            return new FamilyImportResult(
                Success: true,
                CatalogItemId: request.CatalogItemId,
                VersionId: existingVersion.Id,
                FileId: existingVersion.FileId,
                FileName: metadata.FileName,
                VersionLabel: existingVersion.VersionLabel,
                ErrorMessage: null,
                WasSkippedAsDuplicate: true);
        }

        var versionLabel = await GetNextVersionLabelAsync(request.CatalogItemId, ct);
        var newName = Path.GetFileNameWithoutExtension(filePath);
        var normalizedName = FamilyNameNormalizer.Normalize(newName);
        var now = DateTimeOffset.UtcNow;
        var fileRecordId = Guid.NewGuid().ToString();
        var versionId = Guid.NewGuid().ToString();

        var copyResult = await CopyToManagedStorageAsync(filePath, request.CatalogItemId, versionLabel, revitVersion, metadata);
        if (!copyResult.Success)
            return new FamilyImportResult(
                Success: false,
                CatalogItemId: null,
                VersionId: null,
                FileId: null,
                FileName: metadata.FileName,
                VersionLabel: null,
                ErrorMessage: copyResult.ErrorMessage);

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
                await InsertFileRecordAsync(connection, fileRecordId, copyResult.RelativePath!, metadata, revitVersion, now, ct);
                await UpdateCatalogItemWithNameAsync(connection, request.CatalogItemId, newName, normalizedName, versionLabel, now, ct);
                await InsertVersionAsync(connection, versionId, request.CatalogItemId, fileRecordId, versionLabel, metadata, revitVersion, now, ct);

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
                CatalogItemId: request.CatalogItemId,
                VersionId: versionId,
                FileId: fileRecordId,
                FileName: metadata.FileName,
                VersionLabel: versionLabel,
                ErrorMessage: null,
                WasNewVersion: true);
        }
        catch
        {
            await CleanupFileAsync(copyResult.RelativePath);
            throw;
        }
    }

    private async Task<CopyResult> CopyToManagedStorageAsync(string sourcePath, string catalogItemId, string versionLabel, int revitVersion, FamilyMetadataExtractionResult metadata)
    {
        try
        {
            _pathResolver.EnsureFamilyDirectories(catalogItemId, versionLabel, revitVersion);
            var absolutePath = _pathResolver.GetRfaFilePath(catalogItemId, versionLabel, revitVersion, metadata.FileName);
            File.Copy(sourcePath, absolutePath, overwrite: true);
            File.SetAttributes(absolutePath, File.GetAttributes(absolutePath) | FileAttributes.ReadOnly);
            var relativePath = _pathResolver.GetRelativePath(absolutePath);
            SmartConLogger.Info($"[Import] Copied to managed storage (read-only): {absolutePath}");
            return new CopyResult(true, relativePath, null);
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"[Import] Copy FAILED: {ex.Message}");
            return new CopyResult(false, null, $"Failed to copy file to managed storage: {ex.Message}");
        }
    }

    private async Task CleanupFileAsync(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return;
        try
        {
            var absPath = Path.Combine(_database.GetDatabaseRoot(), relativePath);
            if (File.Exists(absPath))
                File.Delete(absPath);
        }
        catch
        {
            // ignored
        }
    }

    private readonly record struct CopyResult(bool Success, string? RelativePath, string? ErrorMessage);
}

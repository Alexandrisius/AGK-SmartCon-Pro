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
    private readonly ILocalCatalogMigrator _migrator;
    private readonly LocalCatalogProvider _catalogProvider;
    private readonly StoragePathResolver _pathResolver;
    private readonly IFamilyMetadataExtractionService _metadataService;
    private readonly IFamilyTypeCatalogBaker _typeCatalogBaker;
    private readonly IRevitFileInfoReader? _fileInfoReader;

    public LocalFamilyImportService(
        LocalCatalogDatabase database,
        ILocalCatalogMigrator migrator,
        LocalCatalogProvider catalogProvider,
        StoragePathResolver pathResolver,
        IFamilyMetadataExtractionService metadataService,
        IFamilyTypeRepository typeRepository,
        IAttributeValueRepository valueRepository,
        IFamilyDataImportRunRepository runRepository,
        IFamilyTypeCatalogBaker typeCatalogBaker,
        IRevitFileInfoReader? fileInfoReader = null,
        IFamilyGeometryPipeline? geometryPipeline = null)
    {
        _database = database;
        _migrator = migrator;
        _catalogProvider = catalogProvider;
        _pathResolver = pathResolver;
        _metadataService = metadataService;
        _typeCatalogBaker = typeCatalogBaker;
        _typeRepository = typeRepository;
        _valueRepository = valueRepository;
        _runRepository = runRepository;
        _fileInfoReader = fileInfoReader;
        _geometryPipeline = geometryPipeline;
    }

    public async Task<FamilyImportResult> ImportFileAsync(FamilyImportRequest request, CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("LocalImport",
            ("Method", "ImportFileAsync"),
            ("FileName", Path.GetFileName(request.FilePath)));

        await _migrator.MigrateAsync(ct);

        var filePath = request.FilePath;
        SmartConLogger.Debug($"ImportFileAsync called with FilePath='{filePath}', exists={File.Exists(filePath)}");
        if (!File.Exists(filePath))
        {
            SmartConLogger.Warn(
                $"ImportFileAsync: file not found at '{filePath}'. " +
                "This usually means the orchestrator's pre-dialog staging did not run " +
                "and the item still has its virtual placeholder FilePath. " +
                "[Action: see StageSystemFamiliesFromMetadataAsync / " +
                "StageLoadableFamiliesFromMetadataAsync log lines for the real cause]");
            return new FamilyImportResult(
                Success: false,
                CatalogItemId: null,
                VersionId: null,
                FileId: null,
                FileName: Path.GetFileName(filePath),
                VersionLabel: null,
                ErrorMessage: $"File not found: {filePath}");
        }

        var sourceMetadata = await _metadataService.ExtractAsync(filePath, ct);
        var revitVersion = _fileInfoReader?.ReadRevitVersion(filePath) ?? request.RevitMajorVersion;

        var displayName = !string.IsNullOrWhiteSpace(request.FileName)
            ? request.FileName!
            : SafeFileName.GetBaseName(filePath);

        SmartConLogger.Debug($"File: {Path.GetFileName(filePath)} -> displayName='{displayName}', Revit: R{revitVersion}");

        // v2.0.0: resolve the canonical (catalogItemId, versionLabel,
        // managedRfaPath) tuple. When the caller pre-computed them
        // (UC-2 SaveAs / UC-3 / UC-4 staged flow), trust them — they
        // already encoded the existing-vs-new decision and the
        // next-version lookup, and the staged file already lives at
        // PrecomputedManagedPath. For the legacy / direct import path
        // (UC-1 user .rfa files), fall back to a fresh allocation.
        var existingItem = await FindByNameAsync(displayName, ct);

        var hasPrecomputed =
            !string.IsNullOrWhiteSpace(request.PrecomputedCatalogItemId)
            && !string.IsNullOrWhiteSpace(request.PrecomputedVersionLabel)
            && !string.IsNullOrWhiteSpace(request.PrecomputedManagedPath);

        SmartConLogger.Debug(
            $"ImportFileAsync: hasPrecomputed={hasPrecomputed}, " +
            $"req.PrecomputedCatalogItemId='{request.PrecomputedCatalogItemId ?? "<null>"}', " +
            $"req.PrecomputedManagedPath='{request.PrecomputedManagedPath ?? "<null>"}'");

        string catalogItemId;
        string versionLabel;
        string managedRfaPath;

        if (hasPrecomputed)
        {
            catalogItemId = request.PrecomputedCatalogItemId!;
            versionLabel = request.PrecomputedVersionLabel!;
            managedRfaPath = request.PrecomputedManagedPath!;

            SmartConLogger.Debug(
                $"ImportFileAsync: using precomputed path " +
                $"catalogItemId='{catalogItemId}', versionLabel='{versionLabel}', " +
                $"managedRfaPath='{managedRfaPath}'");

            if (!string.Equals(filePath, managedRfaPath, StringComparison.OrdinalIgnoreCase))
            {
                SmartConLogger.Warn(
                    $"ImportFileAsync: source FilePath '{filePath}' differs from " +
                    $"precomputed managed path '{managedRfaPath}' [Action: verify " +
                    $"MapPreparedItemsToBatchItemsAsync output]");
            }
        }
        else
        {
            // Direct import path (UC-1, ImportFolderAsync). Allocate a
            // fresh catalogItemId (or reuse existingItem.Id) and a fresh
            // version label, then compute the canonical managed path.
            catalogItemId = existingItem?.Id ?? Guid.NewGuid().ToString();
            versionLabel = existingItem is not null
                ? await ComputeNextVersionLabelAsync(existingItem.Id, ct)
                : "v1";
            managedRfaPath = ComputeManagedRfaPath(catalogItemId, versionLabel, sourceMetadata, displayName);
        }

        var now = DateTimeOffset.UtcNow;
        var fileRecordId = Guid.NewGuid().ToString();
        var versionId = Guid.NewGuid().ToString();
        var normalizedName = FamilyNameNormalizer.Normalize(displayName);
        var relativePath = _pathResolver.GetRelativePath(managedRfaPath);

        SmartConLogger.Debug(
            $"ImportFileAsync: catalogItemId='{catalogItemId}', versionLabel='{versionLabel}', " +
            $"managedRfaPath='{managedRfaPath}', existingItem={(existingItem?.Id ?? "<null>")}");

        try
        {
            TypeCatalogResolutionResult? catalogResult = null;

            // PrepareManagedRfaAsync only runs when the source file is NOT
            // already at its canonical managed path. UC-2/UC-3/UC-4 staged
            // flows hand us a path that IS the managed path; running
            // PrepareManagedRfaAsync would either re-copy (waste) or, when
            // the staged path was mis-named, corrupt the layout.
            var sourceIsAlreadyManaged = string.Equals(
                Path.GetFullPath(filePath),
                Path.GetFullPath(managedRfaPath),
                StringComparison.OrdinalIgnoreCase);

            if (!sourceIsAlreadyManaged)
            {
                SmartConLogger.Debug(
                    $"ImportFileAsync: entering PrepareManagedRfaAsync, filePath='{filePath}', managedRfaPath='{managedRfaPath}', exists={File.Exists(managedRfaPath)}");

                catalogResult = await PrepareManagedRfaAsync(
                    filePath,
                    request.OriginalSourcePath,
                    catalogItemId,
                    versionId,
                    versionLabel,
                    managedRfaPath,
                    ct);

                SmartConLogger.Debug(
                    $"ImportFileAsync: PrepareManagedRfaAsync returned, catalogResult={(catalogResult is null ? "null" : "TypeCatalogResolutionResult")}, exists={File.Exists(managedRfaPath)}");

                if (!File.Exists(managedRfaPath))
                {
                    SmartConLogger.Warn(
                        $"ImportFileAsync: managed file missing at '{managedRfaPath}' after PrepareManagedRfaAsync [Action: check staging helpers in StageSystemFamiliesFromMetadataAsync / StageLoadableFamiliesFromMetadataAsync]");
                    return new FamilyImportResult(
                        Success: false,
                        CatalogItemId: null,
                        VersionId: null,
                        FileId: null,
                        FileName: sourceMetadata.FileName,
                        VersionLabel: null,
                        ErrorMessage: "Managed family file was not created after Type Catalog processing");
                }
            }
            else
            {
                SmartConLogger.Debug(
                    $"ImportFileAsync: source already at managed path '{filePath}', skipping PrepareManagedRfaAsync");
            }

            var finalMetadata = await _metadataService.ExtractAsync(managedRfaPath, ct);

            using var connection = _database.CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var tx = connection.BeginTransaction();

            try
            {
                await InsertFileRecordAsync(connection, fileRecordId, relativePath, finalMetadata, revitVersion, now, ct).ConfigureAwait(false);

                if (existingItem is null)
                {
                    await InsertCatalogItemAsync(connection, catalogItemId, displayName, normalizedName, request, now, versionLabel, ct).ConfigureAwait(false);
                }
                else
                {
                    await UpdateCatalogItemVersionAsync(connection, catalogItemId, versionLabel, now, ct,
                        request.ContentHash, request.HashFormatVersion, request.RevitCategory, request.RevitCategoryId).ConfigureAwait(false);
                }

                if (request.Facts is not null)
                {
                    await ReplaceFamilyFactsAsync(connection, catalogItemId, request.Facts, ct).ConfigureAwait(false);
                }

                await InsertVersionAsync(connection, versionId, catalogItemId, fileRecordId, versionLabel, finalMetadata, revitVersion, now, ct,
                    request.ContentHash, request.HashFormatVersion, request.PublishedBy, request.FamilySource).ConfigureAwait(false);

                // #249 (Phase 2): per-type content hashes computed at
                // Prepare. null (legacy/folder import) → rows stay empty,
                // the type-hashes-v1 actualization task backfills them.
                if (request.PerTypeHashes is not null)
                {
                    await ReplaceTypeHashesAsync(connection, versionId, request.PerTypeHashes, now, ct).ConfigureAwait(false);
                }

                // #249 (Phase 4): canonical content sections. null → the
                // section-hashes-v1 actualization task backfills them.
                if (request.Sections is not null)
                {
                    await WriteVersionSectionsAsync(connection, versionId, request.Sections, ct).ConfigureAwait(false);
                }

                if (existingItem is null && request.Tags is not null)
                {
                    foreach (var tag in request.Tags)
                    {
                        await InsertTagAsync(connection, catalogItemId, tag, ct).ConfigureAwait(false);
                    }
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            if (catalogResult is not null)
            {
                await ImportParsedTypeCatalogAsync(
                    catalogResult.ParseResult,
                    catalogResult.SourceTxtPath,
                    catalogItemId,
                    versionId,
                    versionLabel,
                    ct);
            }

            // ADR-034: shared-nested family name persistence now lives in
            // FamilyManagerMainViewModel.ExtractFromManagedFileAsync — see
            // SaveSharedNestedNamesAsync. Co-locating the scan inside the
            // existing IFamilyDataExtractionService.ExtractFromManagedFile
            // open-close cycle keeps the Revit family-upgrade dialog count
            // at 1 per .rfa instead of the V2 baseline of 2 (separate
            // ISHaredNestedFamilyExtractor that opened the file a second
            // time).

            // ADR-042 H1: extract 3D geometry preview for this NEW version.
            await RunGeometryPipelineHookAsync(
                request.PreextractedGeometry,
                managedRfaPath,
                catalogItemId, versionId, versionLabel,
                StripFamilyExtension(finalMetadata.FileName), ct).ConfigureAwait(false);

            return new FamilyImportResult(
                Success: true,
                CatalogItemId: catalogItemId,
                VersionId: versionId,
                FileId: fileRecordId,
                FileName: finalMetadata.FileName,
                VersionLabel: versionLabel,
                ErrorMessage: null,
                ManagedFilePath: managedRfaPath,
                WasNewVersion: existingItem is not null);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"ImportFileAsync threw: {ex.GetType().Name}: {ex.Message} " +
                $"[Action: check file='{Path.GetFileName(filePath)}', managedPath='{managedRfaPath}']");
            SmartConLogger.Debug($"ImportFileAsync stack trace: {ex.StackTrace}");
            CleanupFileAsync(relativePath);
            throw;
        }
    }

    private const int CopyMaxRetries = 3;
    private static readonly int[] CopyRetryDelaysMs = [100, 300, 900];

    private string ComputeManagedRfaPath(string catalogItemId, string versionLabel, FamilyMetadataExtractionResult metadata, string? displayName)
    {
        _pathResolver.EnsureFamilyDirectories(catalogItemId, versionLabel);

        var sourceExt = Path.GetExtension(metadata.FileName);
        if (string.IsNullOrEmpty(sourceExt))
            sourceExt = ".rfa";

        var destFileName = !string.IsNullOrWhiteSpace(displayName)
            ? SafeFileName.SanitizeFileName(displayName) + sourceExt
            : metadata.FileName;

        return _pathResolver.GetRfaFilePath(catalogItemId, versionLabel, destFileName);
    }

    private static async Task<CopyResult> CopyFileWithRetryAsync(string sourcePath, string destPath, CancellationToken ct)
    {
        var fileName = Path.GetFileName(sourcePath);

        for (var attempt = 0; attempt < CopyMaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await Task.Run(() =>
                {
                    using var sourceStream = new FileStream(
                        sourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite);
                    using var destStream = new FileStream(
                        destPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None);
                    sourceStream.CopyTo(destStream);
                    destStream.Flush();
                }, ct);

                try
                {
                    File.SetAttributes(destPath, File.GetAttributes(destPath) | FileAttributes.ReadOnly);
                }
                catch (IOException attrEx) when (attempt < CopyMaxRetries - 1)
                {
                    SmartConLogger.Info($"SetAttributes failed (attempt {attempt + 1}), retrying: {attrEx.Message}");
                    await Task.Delay(CopyRetryDelaysMs[attempt], ct);
                    continue;
                }

                SmartConLogger.Debug($"Copied to managed storage (read-only): {destPath}");
                return new CopyResult(true, null, null);
            }
            catch (IOException) when (attempt < CopyMaxRetries - 1)
            {
                await Task.Delay(CopyRetryDelaysMs[attempt], ct);
            }
            catch (Exception ex)
            {
                SmartConLogger.Info($"Copy FAILED for '{fileName}': {ex.Message}");
                return new CopyResult(false, null, $"Failed to copy file to managed storage: {ex.Message}");
            }
        }

        return new CopyResult(false, null, $"Failed to copy file to managed storage after {CopyMaxRetries} attempts");
    }

    private void CleanupFileAsync(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return;
        try
        {
            var absPath = Path.Combine(_database.GetDatabaseRoot(), relativePath);
            if (File.Exists(absPath))
                File.Delete(absPath);

            // v2.0.0: Type Catalog (.txt) is no longer stored in managed
            // storage — baker (ADR-033) bakes types into the .rfa itself.
        }
        catch
        {
            // ignored
        }
    }

    private readonly record struct CopyResult(bool Success, string? RelativePath, string? ErrorMessage);
}
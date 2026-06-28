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
        IRevitFileInfoReader? fileInfoReader = null)
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
                    $"BuildSystemFamilyBatchRowVirtualAsync / " +
                    $"BuildLoadableFamilyBatchRowVirtualAsync output]");
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
                        request.ContentHash, request.HashFormatVersion).ConfigureAwait(false);
                }

                await InsertVersionAsync(connection, versionId, catalogItemId, fileRecordId, versionLabel, finalMetadata, revitVersion, now, ct,
                    request.ContentHash, request.HashFormatVersion).ConfigureAwait(false);

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

                if (result.WasSkipped)
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

    public async Task<FamilyBatchImportResult> ImportBatchAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        string? categoryId,
        IProgress<FamilyImportProgress>? progress,
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("LocalImport",
            ("Method", "ImportBatchAsync"),
            ("Count", items.Count));

        await _migrator.MigrateAsync(ct);

        var results = new List<FamilyImportResult>();
        var successCount = 0;
        var skippedCount = 0;
        var errorCount = 0;

        // v2.0.0: dedupe by (catalogItemId, versionLabel) so multiple
        // staged rows that resolve to the same canonical managed entry
        // (e.g. two system-family analyses that name the same category,
        // or a user re-running the import twice in the same dialog) only
        // produce ONE catalog_versions row. Otherwise we'd hit a SQLite
        // UNIQUE constraint failure on
        // (catalog_item_id, version_label, revit_major_version).
        var processedKeys = new HashSet<string>();
        var dedupedItems = new List<FamilyBatchImportItem>(items.Count);

        foreach (var item in items)
        {
            if (item.Action == FamilyBatchImportAction.Skip)
            {
                dedupedItems.Add(item);
                continue;
            }

            var key = BuildBatchDedupKey(item);
            if (key is null || processedKeys.Add(key))
            {
                dedupedItems.Add(item);
            }
            else
            {
                SmartConLogger.Warn(
                    $"ImportBatchAsync: dropping duplicate item '{item.FileName}' " +
                    $"for key '{key}' [Action: same family/version already queued in this batch]");
                // We still need to emit a synthetic "skipped" result so
                // the caller's per-item progress stays in sync.
                dedupedItems.Add(item with { Action = FamilyBatchImportAction.Skip });
            }
        }

        for (var i = 0; i < dedupedItems.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = dedupedItems[i];

            SmartConLogger.Info($"File: {item.FileName}, Status: {item.Status}, Action: {item.Action}");

            progress?.Report(new FamilyImportProgress(
                CurrentFileIndex: i,
                TotalFiles: dedupedItems.Count,
                CurrentFileName: item.FileName,
                SuccessCount: successCount,
                SkippedCount: skippedCount,
                ErrorCount: errorCount));

            if (item.Action == FamilyBatchImportAction.Skip)
            {
                skippedCount++;
                results.Add(new FamilyImportResult(
                    Success: true,
                    CatalogItemId: item.ExistingCatalogItemId,
                    VersionId: null,
                    FileId: null,
                    FileName: item.FileName,
                    VersionLabel: item.ExistingVersionLabel,
                    ErrorMessage: null,
                    WasSkipped: true));
                continue;
            }

            try
            {
                FamilyImportResult result;
                if (item.Status == FamilyBatchImportStatus.New)
                {
                    // Source-of-truth rule: when no category is assigned, write
                    // a NULL `Category` (and a NULL `CategoryId`) so the
                    // denormalised catalog_items.category_name column never
                    // accumulates a literal "Без категории" placeholder.
                    // The picker in FamilyBatchImportViewModel writes the
                    // placeholder string into TargetCategoryName when the user
                    // explicitly picks "no category" — the writer must
                    // translate that back to a real NULL, otherwise future
                    // batch dialogs would read back the placeholder and
                    // display it as if it were a real category name.
                    var effectiveCategoryId = item.TargetCategoryId ?? categoryId;
                    var effectiveCategoryName = effectiveCategoryId is null
                        ? null
                        : item.TargetCategoryName;
                    var request = new FamilyImportRequest(
                        FilePath: item.FilePath,
                        RevitMajorVersion: item.RevitMajorVersion,
                        Category: effectiveCategoryName,
                        Tags: null,
                        Description: null,
                        CategoryId: effectiveCategoryId,
                        FamilySource: item.FamilySource,
                        RevitCategory: item.RevitCategory,
                        FileName: item.FileName,
                        OriginalSourcePath: item.OriginalSourcePath,
                        PrecomputedCatalogItemId: item.PrecomputedCatalogItemId,
                        PrecomputedVersionLabel: item.PrecomputedVersionLabel,
                        PrecomputedManagedPath: item.PrecomputedManagedPath);
                    result = await ImportFileAsync(request, ct);
                }
                else
                {
                    if (item.Action == FamilyBatchImportAction.IncrementVersion)
                    {
                        // Source-of-truth rule: when no category is assigned
                        // (TargetCategoryId is null), do not write the picker
                        // placeholder literal into catalog_items.category_name.
                        // The DB column should stay NULL until the user picks
                        // a real category. We pass the real name only when
                        // a real CategoryId is present, mirroring the
                        // FamilyImportRequest branch above. UpdateFamilyAsync
                        // calls UpdateCatalogItemCategoryAsync only when
                        // CategoryId is not empty (Database.cs:443-445), so
                        // passing null here is a no-op — the row's existing
                        // category assignment is preserved.
                        var effectiveCategoryName = item.TargetCategoryId is null
                            ? null
                            : item.TargetCategoryName;
                        var request = new FamilyUpdateRequest(
                            CatalogItemId: item.ExistingCatalogItemId!,
                            FilePath: item.FilePath,
                            RevitMajorVersion: item.RevitMajorVersion,
                            CategoryId: item.TargetCategoryId,
                            CategoryName: effectiveCategoryName,
                            FileName: item.FileName,
                            OriginalSourcePath: item.OriginalSourcePath,
                            PrecomputedVersionLabel: item.PrecomputedVersionLabel,
                            PrecomputedManagedPath: item.PrecomputedManagedPath);
                        result = await UpdateFamilyAsync(request, ct);
                    }
                    else
                    {
                        result = await OverwriteCurrentAsync(item, ct);
                    }
                }

                results.Add(result);
                if (result.Success) successCount++;
                else errorCount++;
            }
            catch (Exception ex)
            {
                errorCount++;
                SmartConLogger.Error(
                    $"ImportBatchAsync: file='{item.FileName}' threw: {ex.GetType().Name}: {ex.Message} " +
                    $"[Action: see prior log lines from ImportFileAsync for the underlying cause]");
                SmartConLogger.Debug($"ImportBatchAsync stack trace: {ex.StackTrace}");
                results.Add(new FamilyImportResult(
                    Success: false,
                    CatalogItemId: null,
                    VersionId: null,
                    FileId: null,
                    FileName: item.FileName,
                    VersionLabel: null,
                    ErrorMessage: ex.Message));
            }
        }

        progress?.Report(new FamilyImportProgress(
            CurrentFileIndex: dedupedItems.Count - 1,
            TotalFiles: dedupedItems.Count,
            CurrentFileName: string.Empty,
            SuccessCount: successCount,
            SkippedCount: skippedCount,
            ErrorCount: errorCount));

        return new FamilyBatchImportResult(
            Results: results,
            TotalFiles: dedupedItems.Count,
            SuccessCount: successCount,
            SkippedCount: skippedCount,
            ErrorCount: errorCount);
    }

    private static string? BuildBatchDedupKey(FamilyBatchImportItem item)
    {
        // v2.0.0: a batch row collides with a previous one when both
        // resolve to the same canonical managed file, i.e. the same
        // (catalogItemId, versionLabel, revit_major_version). Without
        // this guard the second row would trip the SQLite UNIQUE
        // constraint on catalog_versions.
        var id = item.PrecomputedCatalogItemId ?? item.ExistingCatalogItemId;
        var ver = item.PrecomputedVersionLabel
            ?? item.ExistingVersionLabel
            ?? "v1";
        if (string.IsNullOrEmpty(id)) return null;
        return $"{id}|{ver}|{item.RevitMajorVersion}";
    }

    public async Task<FamilyImportResult> UpdateFamilyAsync(FamilyUpdateRequest request, CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("LocalImport",
            ("Method", "UpdateFamilyAsync"),
            ("CatalogItemId", request.CatalogItemId),
            ("FileName", Path.GetFileName(request.FilePath)));

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

        var sourceMetadata = await _metadataService.ExtractAsync(filePath, ct);
        var revitVersion = _fileInfoReader?.ReadRevitVersion(filePath) ?? request.RevitMajorVersion;

        var newName = !string.IsNullOrWhiteSpace(request.FileName)
            ? request.FileName!
            : SafeFileName.GetBaseName(filePath);

        SmartConLogger.Info($"File: {Path.GetFileName(filePath)} -> newName='{newName}', Revit: R{revitVersion}, TargetItem: {request.CatalogItemId}");

        // v2.0.0: SHA-256 dedup is gone. UpdateFamilyAsync always creates a new
        // version (vN+1). Callers that want to replace the current version
        // should use OverwriteCurrent action via ImportBatchAsync instead.

        var normalizedName = FamilyNameNormalizer.Normalize(newName);
        var now = DateTimeOffset.UtcNow;
        var fileRecordId = Guid.NewGuid().ToString();
        var versionId = Guid.NewGuid().ToString();

        // v2.0.0: prefer caller-supplied (versionLabel, managedRfaPath)
        // so the staged file the VM placed at the canonical path can be
        // registered without re-allocating. Fall back to a fresh
        // (versionLabel, managedRfaPath) when the caller didn't precompute
        // (legacy / direct callers, tests).
        var hasPrecomputed =
            !string.IsNullOrWhiteSpace(request.PrecomputedVersionLabel)
            && !string.IsNullOrWhiteSpace(request.PrecomputedManagedPath);

        string versionLabel;
        string managedRfaPath;

        if (hasPrecomputed)
        {
            versionLabel = request.PrecomputedVersionLabel!;
            managedRfaPath = request.PrecomputedManagedPath!;
        }
        else
        {
            versionLabel = await ComputeNextVersionLabelAsync(request.CatalogItemId, ct);
            managedRfaPath = ComputeManagedRfaPath(request.CatalogItemId, versionLabel, sourceMetadata, newName);
        }

        var relativePath = _pathResolver.GetRelativePath(managedRfaPath);

        try
        {
            TypeCatalogResolutionResult? catalogResult = null;
            var sourceIsAlreadyManaged = string.Equals(
                Path.GetFullPath(filePath),
                Path.GetFullPath(managedRfaPath),
                StringComparison.OrdinalIgnoreCase);

            if (!sourceIsAlreadyManaged)
            {
                catalogResult = await PrepareManagedRfaAsync(
                    filePath,
                    request.OriginalSourcePath,
                    request.CatalogItemId,
                    versionId,
                    versionLabel,
                    managedRfaPath,
                    ct);

                if (!File.Exists(managedRfaPath))
                {
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

            var finalMetadata = await _metadataService.ExtractAsync(managedRfaPath, ct);

            using var connection = _database.CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var tx = connection.BeginTransaction();

            try
            {
                await InsertFileRecordAsync(connection, fileRecordId, relativePath, finalMetadata, revitVersion, now, ct).ConfigureAwait(false);
                await UpdateCatalogItemWithNameAsync(connection, request.CatalogItemId, newName, normalizedName, versionLabel, now, ct,
                    request.ContentHash, request.HashFormatVersion).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(request.CategoryId))
                {
                    await UpdateCatalogItemCategoryAsync(connection, request.CatalogItemId, request.CategoryId, request.CategoryName, now, ct).ConfigureAwait(false);
                }
                await InsertVersionAsync(connection, versionId, request.CatalogItemId, fileRecordId, versionLabel, finalMetadata, revitVersion, now, ct,
                    request.ContentHash, request.HashFormatVersion).ConfigureAwait(false);

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
                    request.CatalogItemId,
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

            return new FamilyImportResult(
                Success: true,
                CatalogItemId: request.CatalogItemId,
                VersionId: versionId,
                FileId: fileRecordId,
                FileName: finalMetadata.FileName,
                VersionLabel: versionLabel,
                ErrorMessage: null,
                ManagedFilePath: managedRfaPath,
                WasNewVersion: true);
        }
        catch
        {
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
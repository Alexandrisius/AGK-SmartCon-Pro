using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed partial class LocalFamilyImportService
{
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
                    request.ContentHash, request.HashFormatVersion, request.RevitCategory, request.RevitCategoryId).ConfigureAwait(false);
                if (request.Facts is not null)
                {
                    await ReplaceFamilyFactsAsync(connection, request.CatalogItemId, request.Facts, ct).ConfigureAwait(false);
                }
                // #261: an explicit «Без категории» pick writes a real NULL
                // (ClearCategory) — moving the item out of its category. A
                // null CategoryId WITHOUT the flag stays a no-op: "no explicit
                // choice" must not touch the existing assignment.
                if (!string.IsNullOrEmpty(request.CategoryId) || request.ClearCategory)
                {
                    await UpdateCatalogItemCategoryAsync(connection, request.CatalogItemId, request.CategoryId, request.CategoryName, now, ct).ConfigureAwait(false);
                }
                await InsertVersionAsync(connection, versionId, request.CatalogItemId, fileRecordId, versionLabel, finalMetadata, revitVersion, now, ct,
                    request.ContentHash, request.HashFormatVersion, request.PublishedBy).ConfigureAwait(false);

                // #249 (Phase 2): per-type content hashes — see ImportFileAsync.
                if (request.PerTypeHashes is not null)
                {
                    await ReplaceTypeHashesAsync(connection, versionId, request.PerTypeHashes, now, ct).ConfigureAwait(false);
                }

                // #249 (Phase 4): canonical content sections — see ImportFileAsync.
                if (request.Sections is not null)
                {
                    await WriteVersionSectionsAsync(connection, versionId, request.Sections, ct).ConfigureAwait(false);
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

            // ADR-042 H2: extract 3D geometry preview for this INCREMENTED version.
            await RunGeometryPipelineHookAsync(
                request.PreextractedGeometry,
                managedRfaPath,
                request.CatalogItemId!, versionId, versionLabel,
                StripFamilyExtension(finalMetadata.FileName), ct).ConfigureAwait(false);

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
}

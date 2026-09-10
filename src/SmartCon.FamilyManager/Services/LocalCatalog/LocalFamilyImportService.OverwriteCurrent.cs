using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed partial class LocalFamilyImportService
{
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
    /// ADR-040: also UPDATEs catalog_versions (content_hash, types_count,
    /// parameters_count, published_at_utc) in place so stale detection and
    /// the catalog UI reflect the new content. The .rfa/.rvt file at the
    /// current version's managed path is replaced on disk; no new
    /// catalog_versions row is inserted.
    /// </summary>
    private async Task<FamilyImportResult> OverwriteCurrentAsync(FamilyBatchImportItem item, CancellationToken ct)
    {
        var currentVersion = await FindCurrentVersionAsync(item.ExistingCatalogItemId!, ct);
        if (currentVersion is null)
        {
            SmartConLogger.Warn(
                $"OverwriteCurrentAsync: current version not found for catalogItemId='{item.ExistingCatalogItemId}' " +
                $"[Action: проверьте, что catalog_items.current_version_label указывает на существующую catalog_versions строку]");
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

        // #252: capture the PRE-OVERWRITE section hashes of this version —
        // the geometry pipeline hook (H3) uses them as the reuse baseline;
        // after the UPDATE below the row carries the NEW sections and the
        // honest "did the preview content actually change?" answer would be
        // lost (the tier-1 check can only see OTHER versions, which almost
        // always differ and forced a full mesh extraction on every
        // text-only overwrite).
        string? preOverwriteSectionsJson;
        using (var sectionsCmd = connection.CreateCommand())
        {
            sectionsCmd.Transaction = tx;
            sectionsCmd.CommandText = "SELECT section_hashes FROM catalog_versions WHERE id = @versionId";
            sectionsCmd.Parameters.Add(new SqliteParameter("@versionId", currentVersion.Id));
            preOverwriteSectionsJson = Convert.ToString(await sectionsCmd.ExecuteScalarAsync(ct));
        }
        var preOverwriteSections = ContentSectionJsonSerializer.Deserialize(preOverwriteSectionsJson);

        // Get current file path
        using var pathCmd = connection.CreateCommand();
        pathCmd.CommandText = "SELECT relative_path FROM family_files WHERE id = @fileId";
        pathCmd.Parameters.Add(new SqliteParameter("@fileId", currentVersion.FileId));
        var relativePath = await pathCmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrEmpty(relativePath))
        {
            tx.Rollback();
            SmartConLogger.Warn(
                $"OverwriteCurrentAsync: current file path not found for fileId='{currentVersion.FileId}' " +
                $"[Action: проверьте family_files.relative_path для текущей версии]");
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
        TypeCatalogResolutionResult? catalogResult = null;

        try
        {
            catalogResult = await PrepareManagedRfaAsync(
                item.FilePath,
                item.OriginalSourcePath,
                item.ExistingCatalogItemId!,
                currentVersion.Id,
                currentVersion.VersionLabel,
                absolutePath,
                ct);

            if (!File.Exists(absolutePath))
            {
                tx.Rollback();
                return new FamilyImportResult(
                    Success: false,
                    CatalogItemId: item.ExistingCatalogItemId,
                    VersionId: null,
                    FileId: null,
                    FileName: item.FileName,
                    VersionLabel: null,
                    ErrorMessage: "Managed family file was not created after Type Catalog processing");
            }

            // ADR-040: extract final metadata from the overwritten file so
            // types_count/parameters_count reflect the new content. The
            // FileMetadataExtractionService is a lightweight FileInfo-based
            // reader (no Revit API); Types/Parameters may be null, which
            // UpdateVersionAsync translates to DBNull (matching ImportFileAsync
            // behaviour). content_hash/hash_format_version come from the
            // Prepare-phase computation (item.ContentHash/HashFormatVersion).
            var finalMetadata = await _metadataService.ExtractAsync(absolutePath, ct);
            var now = DateTimeOffset.UtcNow;

            // ADR-040: determine the file extension from FamilySource so
            // system families (.rvt) get the correct file_name in
            // family_files. Previously this was hardcoded to ".rfa", which
            // produced "Трубы.rfa" for a system Pipe family.
            var extension = string.Equals(item.FamilySource, "system", StringComparison.OrdinalIgnoreCase)
                ? ".rvt"
                : ".rfa";

            // Update family_files (v2.0.0: no sha256/size_bytes columns)
            using var updateFileCmd = connection.CreateCommand();
            updateFileCmd.CommandText = """
                UPDATE family_files
                SET file_name = @fileName, imported_at_utc = @importedAtUtc
                WHERE id = @fileId
                """;
            updateFileCmd.Parameters.Add(new SqliteParameter("@fileId", currentVersion.FileId));
            // v2.0.1: write the user-edited name + extension to the
            // family_files.file_name column so a rename in the batch
            // dialog is reflected in the file_name too. Previously this
            // was sourceMetadata.FileName (the staged source file),
            // which could disagree with the catalog row's name after
            // a rename.
            updateFileCmd.Parameters.Add(new SqliteParameter("@fileName", item.FileName + extension));
            updateFileCmd.Parameters.Add(new SqliteParameter("@importedAtUtc", now.ToString("o")));
            await updateFileCmd.ExecuteNonQueryAsync(ct);

            // v2.0.1: always reflect the user-edited FileName in the
            // catalog row. The original code only updated updated_at_utc
            // (or category) and left catalog_items.name on its original
            // value, so a rename + OverwriteCurrent wrote a new file at
            // the renamed path but the catalog row kept the old name.
            // ADR-040: also update content_hash/hash_format_version so the
            // catalog item reflects the new content for stale detection.
            var normalizedNewName = FamilyNameNormalizer.Normalize(item.FileName);
            await UpdateCatalogItemWithNameAsync(connection, item.ExistingCatalogItemId!, item.FileName, normalizedNewName, currentVersion.VersionLabel, now, ct,
                item.ContentHash, item.HashFormatVersion, item.RevitCategory,
                item.LoadableSnapshot?.CategoryId ?? item.SystemSnapshot?.CategoryId);

            // ADR-055: an overwrite re-extracted the snapshot — refresh the
            // facts too (the family may have changed Part Type). Skip when
            // no snapshot survived the dialog (legacy path).
            var overwriteFacts = item.LoadableSnapshot?.Facts;
            if (overwriteFacts is not null)
            {
                await ReplaceFamilyFactsAsync(connection, item.ExistingCatalogItemId!, overwriteFacts, ct);
            }

            // Update category_id + category_name if the user picked one.
            // #261: an explicit «Без категории» pick (ClearCategoryOnImport)
            // writes a real NULL — same move semantics as the IncrementVersion
            // branch; a plain null TargetCategoryId stays a no-op.
            if (!string.IsNullOrEmpty(item.TargetCategoryId) || item.ClearCategoryOnImport)
            {
                var overwriteCategoryName = item.TargetCategoryId is null
                    ? null
                    : item.TargetCategoryName;
                await UpdateCatalogItemCategoryAsync(connection, item.ExistingCatalogItemId!, item.TargetCategoryId, overwriteCategoryName, now, ct);
            }

            // ADR-040: UPDATE catalog_versions in place (no new row).
            // content_hash, hash_format_version, types_count,
            // parameters_count and published_at_utc reflect the new
            // content; id/version_label/revit_major_version stay the same
            // so FK references (family_types.version_id) remain valid.
            await UpdateVersionAsync(connection, currentVersion.Id, finalMetadata, now,
                item.ContentHash, item.HashFormatVersion, item.PublishedByUser, ct, item.FamilySource);

            // #249 (Phase 2): the overwrite REPLACED the content, so the
            // previous per-type hashes are invalid by definition. Replace
            // them with the freshly computed set; when the dialog lost the
            // snapshot (legacy path, null) the rows are cleared so the
            // type-hashes-v1 actualization task re-detects the version as
            // pending instead of serving stale hashes.
            await ReplaceTypeHashesAsync(connection, currentVersion.Id, item.PerTypeHashes, now, ct);

            // #249 (Phase 4): same rule for the content sections — the
            // overwrite invalidated them; a null set clears the columns so
            // the section-hashes-v1 task re-detects the version.
            await WriteVersionSectionsAsync(connection, currentVersion.Id, item.Sections, ct);

            tx.Commit();

            SmartConLogger.Info(
                $"OverwriteCurrent: updated catalog_versions id='{currentVersion.Id}', " +
                $"versionLabel='{currentVersion.VersionLabel}', " +
                $"content_hash='{item.ContentHash ?? "<null>"}', " +
                $"types_count={(finalMetadata.Types is not null ? finalMetadata.Types.Count.ToString() : "<null>")}, " +
                $"file_name='{item.FileName + extension}'");
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
                item.ExistingCatalogItemId!,
                currentVersion.Id,
                currentVersion.VersionLabel,
                ct);
        }

        // ADR-042 H3: extract 3D geometry preview for this OVERWRITTEN version.
        await RunGeometryPipelineHookAsync(
            null,
            absolutePath,
            item.ExistingCatalogItemId!, currentVersion.Id, currentVersion.VersionLabel,
            StripFamilyExtension(item.FileName), ct, preOverwriteSections).ConfigureAwait(false);

        return new FamilyImportResult(
            Success: true,
            CatalogItemId: item.ExistingCatalogItemId,
            VersionId: currentVersion.Id,
            FileId: currentVersion.FileId,
            // v2.0.1: report the user-edited file name so the UI sees
            // the renamed file (sourceMetadata.FileName was the staged
            // source file, not the edited display name).
            FileName: item.FileName,
            VersionLabel: currentVersion.VersionLabel,
            ErrorMessage: null,
            ManagedFilePath: absolutePath,
            WasNewVersion: false);
        }
}

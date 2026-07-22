using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

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

    private async Task<string> ComputeNextVersionLabelAsync(string catalogItemId, CancellationToken ct)
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

    /// <summary>
    /// v2.0.0: public entry point for <see cref="IFamilyImportService.GetNextVersionLabelAsync"/>.
    /// Returns <c>vN+1</c> for an existing item that has <c>vN</c>,
    /// or <c>"v2"</c> as a defensive fallback when no prior version exists
    /// (the VM uses this to allocate the canonical managed path up front).
    /// </summary>
    public async Task<string> GetNextVersionLabelAsync(string catalogItemId, CancellationToken ct)
    {
        return await ComputeNextVersionLabelAsync(catalogItemId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// v2.0.0: single source of truth for canonical managed paths.
    /// Staging helpers and <see cref="ImportFileAsync"/> both go through
    /// here so the on-disk path and the <c>family_files.relative_path</c>
    /// row can never drift out of sync.
    ///
    /// Returns <c>null</c> when no active database is selected — callers
    /// must surface this as a user error rather than falling back to a
    /// temp folder (v2.0.0 has no temp folders, per ADR-035).
    /// </summary>
    public string? ComputeManagedFilePath(
        string catalogItemId,
        string versionLabel,
        string fileName,
        string extension)
    {
        if (string.IsNullOrEmpty(catalogItemId)) return null;
        if (string.IsNullOrEmpty(versionLabel)) return null;
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var ext = string.IsNullOrEmpty(extension) ? ".rfa" : extension;
        // CA1865 ("use StartsWith(char)") would break net48 compatibility
        // (net48 only exposes StartsWith(string)). Inspecting the first
        // character is portable across both target frameworks.
        if (ext.Length == 0 || ext[0] != '.') ext = "." + ext;

        var dbRoot = _database.GetDatabaseRoot();
        if (string.IsNullOrEmpty(dbRoot)) return null;

        return _pathResolver.GetRfaFilePath(
            catalogItemId,
            versionLabel,
            fileName + ext);
    }

    private static async Task InsertFileRecordAsync(SqliteConnection connection, string id,
        string relativePath, FamilyMetadataExtractionResult metadata,
        int revitVersion, DateTimeOffset now, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
            VALUES (@id, @relativePath, @fileName, @revitVersion, @importedAtUtc)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@relativePath", relativePath));
        cmd.Parameters.Add(new SqliteParameter("@fileName", metadata.FileName));
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
            INSERT INTO catalog_items (id, name, normalized_name, description, category_name, category_id, manufacturer, content_status, current_version_label, published_by, family_source, revit_category, content_hash, hash_format_version, created_at_utc, updated_at_utc)
            VALUES (@id, @name, @normalizedName, @description, @categoryName, @categoryId, @manufacturer, @status, @versionLabel, @publishedBy, @familySource, @revitCategory, @contentHash, @hashFmt, @createdAtUtc, @updatedAtUtc)
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
        cmd.Parameters.Add(new SqliteParameter("@contentHash", request.ContentHash ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@hashFmt", request.HashFormatVersion ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@createdAtUtc", now.ToString("o")));
        cmd.Parameters.Add(new SqliteParameter("@updatedAtUtc", now.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpdateCatalogItemWithNameAsync(SqliteConnection connection, string id,
        string newName, string normalizedName, string versionLabel, DateTimeOffset now, CancellationToken ct,
        string? contentHash = null, int? hashFormatVersion = null, string? revitCategory = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE catalog_items
            SET name = @name, normalized_name = @normalizedName, current_version_label = @versionLabel,
                content_hash = COALESCE(@contentHash, content_hash),
                hash_format_version = COALESCE(@hashFmt, hash_format_version),
                revit_category = COALESCE(revit_category, @revitCategory),
                updated_at_utc = @updatedAtUtc
            WHERE id = @id
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@name", newName));
        cmd.Parameters.Add(new SqliteParameter("@normalizedName", normalizedName));
        cmd.Parameters.Add(new SqliteParameter("@versionLabel", versionLabel));
        cmd.Parameters.Add(new SqliteParameter("@contentHash", contentHash ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@hashFmt", hashFormatVersion ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@revitCategory", revitCategory ?? (object)DBNull.Value));
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
        string versionLabel, DateTimeOffset now, CancellationToken ct,
        string? contentHash = null, int? hashFormatVersion = null, string? revitCategory = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE catalog_items
            SET current_version_label = @versionLabel,
                content_hash = COALESCE(@contentHash, content_hash),
                hash_format_version = COALESCE(@hashFmt, hash_format_version),
                revit_category = COALESCE(revit_category, @revitCategory),
                updated_at_utc = @updatedAtUtc
            WHERE id = @id
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@versionLabel", versionLabel));
        cmd.Parameters.Add(new SqliteParameter("@contentHash", contentHash ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@hashFmt", hashFormatVersion ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@revitCategory", revitCategory ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@updatedAtUtc", now.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertVersionAsync(SqliteConnection connection, string versionId,
        string catalogItemId, string fileId, string versionLabel,
        FamilyMetadataExtractionResult metadata, int revitVersion,
        DateTimeOffset now, CancellationToken ct,
        string? contentHash = null, int? hashFormatVersion = null,
        string? publishedBy = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, revit_major_version, types_count, parameters_count, content_hash, hash_format_version, published_at_utc, published_by)
            VALUES (@id, @catalogItemId, @fileId, @versionLabel, @revitMajorVersion, @typesCount, @parametersCount, @contentHash, @hashFmt, @publishedAtUtc, @publishedBy)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", versionId));
        cmd.Parameters.Add(new SqliteParameter("@catalogItemId", catalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
        cmd.Parameters.Add(new SqliteParameter("@versionLabel", versionLabel));
        cmd.Parameters.Add(new SqliteParameter("@revitMajorVersion", revitVersion));
        cmd.Parameters.Add(new SqliteParameter("@typesCount",
            metadata.Types is not null ? (object)metadata.Types.Count : DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@parametersCount",
            metadata.Parameters is not null ? (object)metadata.Parameters.Count : DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@contentHash", contentHash ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@hashFmt", hashFormatVersion ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@publishedAtUtc", now.ToString("o")));
        cmd.Parameters.Add(new SqliteParameter("@publishedBy", publishedBy ?? (object)DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ADR-040: Updates the catalog_versions row for the current version
    /// in place (no new row, no version_label change). Used by
    /// <see cref="OverwriteCurrentAsync"/> to reflect the new content_hash,
    /// types_count, parameters_count and published_at_utc after the managed
    /// .rfa/.rvt file was overwritten on disk.
    /// </summary>
    private static async Task UpdateVersionAsync(SqliteConnection connection,
        string versionId, FamilyMetadataExtractionResult metadata,
        DateTimeOffset now,
        string? contentHash, int? hashFormatVersion,
        string? publishedBy,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE catalog_versions
            SET types_count = @typesCount,
                parameters_count = @parametersCount,
                content_hash = @contentHash,
                hash_format_version = @hashFmt,
                published_at_utc = @publishedAtUtc,
                published_by = @publishedBy
            WHERE id = @versionId
            """;
        cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
        cmd.Parameters.Add(new SqliteParameter("@typesCount",
            metadata.Types is not null ? (object)metadata.Types.Count : DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@parametersCount",
            metadata.Parameters is not null ? (object)metadata.Parameters.Count : DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@contentHash", contentHash ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@hashFmt", hashFormatVersion ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@publishedAtUtc", now.ToString("o")));
        cmd.Parameters.Add(new SqliteParameter("@publishedBy", publishedBy ?? (object)DBNull.Value));
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
                item.ContentHash, item.HashFormatVersion, item.RevitCategory);

            // Update category_id + category_name if the user picked one.
            if (!string.IsNullOrEmpty(item.TargetCategoryId))
            {
                await UpdateCatalogItemCategoryAsync(connection, item.ExistingCatalogItemId!, item.TargetCategoryId, item.TargetCategoryName, now, ct);
            }

            // ADR-040: UPDATE catalog_versions in place (no new row).
            // content_hash, hash_format_version, types_count,
            // parameters_count and published_at_utc reflect the new
            // content; id/version_label/revit_major_version stay the same
            // so FK references (family_types.version_id) remain valid.
            await UpdateVersionAsync(connection, currentVersion.Id, finalMetadata, now,
                item.ContentHash, item.HashFormatVersion, item.PublishedByUser, ct);

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
            StripFamilyExtension(item.FileName), ct).ConfigureAwait(false);

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

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
            INSERT INTO catalog_items (id, name, normalized_name, description, category_name, category_id, manufacturer, content_status, current_version_label, published_by, family_source, revit_category, revit_category_id, content_hash, hash_format_version, created_at_utc, updated_at_utc)
            VALUES (@id, @name, @normalizedName, @description, @categoryName, @categoryId, @manufacturer, @status, @versionLabel, @publishedBy, @familySource, @revitCategory, @revitCategoryId, @contentHash, @hashFmt, @createdAtUtc, @updatedAtUtc)
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
        cmd.Parameters.Add(new SqliteParameter("@revitCategoryId", request.RevitCategoryId ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@contentHash", request.ContentHash ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@hashFmt", request.HashFormatVersion ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@createdAtUtc", now.ToString("o")));
        cmd.Parameters.Add(new SqliteParameter("@updatedAtUtc", now.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpdateCatalogItemWithNameAsync(SqliteConnection connection, string id,
        string newName, string normalizedName, string versionLabel, DateTimeOffset now, CancellationToken ct,
        string? contentHash = null, int? hashFormatVersion = null, string? revitCategory = null,
        int? revitCategoryId = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE catalog_items
            SET name = @name, normalized_name = @normalizedName, current_version_label = @versionLabel,
                content_hash = COALESCE(@contentHash, content_hash),
                hash_format_version = COALESCE(@hashFmt, hash_format_version),
                revit_category = COALESCE(revit_category, @revitCategory),
                revit_category_id = COALESCE(revit_category_id, @revitCategoryId),
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
        cmd.Parameters.Add(new SqliteParameter("@revitCategoryId", revitCategoryId ?? (object)DBNull.Value));
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
        string? contentHash = null, int? hashFormatVersion = null, string? revitCategory = null,
        int? revitCategoryId = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE catalog_items
            SET current_version_label = @versionLabel,
                content_hash = COALESCE(@contentHash, content_hash),
                hash_format_version = COALESCE(@hashFmt, hash_format_version),
                revit_category = COALESCE(revit_category, @revitCategory),
                revit_category_id = COALESCE(revit_category_id, @revitCategoryId),
                updated_at_utc = @updatedAtUtc
            WHERE id = @id
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@versionLabel", versionLabel));
        cmd.Parameters.Add(new SqliteParameter("@contentHash", contentHash ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@hashFmt", hashFormatVersion ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@revitCategory", revitCategory ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@revitCategoryId", revitCategoryId ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@updatedAtUtc", now.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the item's category-driven facts (ADR-055): DELETE + INSERT
    /// inside the caller's transaction. Called only when the import actually
    /// extracted facts (<paramref name="facts"/> non-null) — a null list
    /// means "no snapshot available" (folder import, legacy callers) and
    /// leaves existing rows for the actualization task.
    /// </summary>
    private static async Task ReplaceFamilyFactsAsync(
        SqliteConnection connection, string catalogItemId,
        IReadOnlyList<FamilyFact> facts, CancellationToken ct)
    {
        using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.CommandText = "DELETE FROM family_facts WHERE catalog_item_id = @itemId";
            deleteCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var fact in facts)
        {
            using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO family_facts (catalog_item_id, fact_key, value_key, value_display)
                VALUES (@itemId, @factKey, @valueKey, @valueDisplay)
                """;
            insertCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            insertCmd.Parameters.Add(new SqliteParameter("@factKey", fact.FactKey));
            insertCmd.Parameters.Add(new SqliteParameter("@valueKey", fact.ValueKey));
            insertCmd.Parameters.Add(new SqliteParameter("@valueDisplay", fact.ValueDisplay));
            await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task InsertVersionAsync(SqliteConnection connection, string versionId,
        string catalogItemId, string fileId, string versionLabel,
        FamilyMetadataExtractionResult metadata, int revitVersion,
        DateTimeOffset now, CancellationToken ct,
        string? contentHash = null, int? hashFormatVersion = null,
        string? publishedBy = null, string? familySource = null)
    {
        using var cmd = connection.CreateCommand();
        // #189: staged system versions carry the mini-project ES marker from
        // staging (#188) — register them as already marked so the
        // mini-project-marker-v1 task only ever touches LEGACY files.
        // The update path (FamilyUpdateRequest) does not carry the source —
        // resolved from the owning item when not supplied.
        if (familySource is null)
        {
            using var sourceCmd = connection.CreateCommand();
            sourceCmd.CommandText = "SELECT family_source FROM catalog_items WHERE id = @itemId";
            sourceCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            familySource = Convert.ToString(await sourceCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }
        cmd.CommandText = """
            INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, revit_major_version, types_count, parameters_count, content_hash, hash_format_version, es_marker_version, published_at_utc, published_by)
            VALUES (@id, @catalogItemId, @fileId, @versionLabel, @revitMajorVersion, @typesCount, @parametersCount, @contentHash, @hashFmt, @esMarker, @publishedAtUtc, @publishedBy)
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
        cmd.Parameters.Add(new SqliteParameter("@esMarker", familySource == "system" ? 1 : 0));
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
        CancellationToken ct, string? familySource = null)
    {
        using var cmd = connection.CreateCommand();
        // #189 (review m1): an overwrite re-stages the file WITH the marker —
        // register it so a legacy 0/-1/-2 cell converges to the truth.
        cmd.CommandText = """
            UPDATE catalog_versions
            SET types_count = @typesCount,
                parameters_count = @parametersCount,
                content_hash = @contentHash,
                hash_format_version = @hashFmt,
                es_marker_version = CASE WHEN @esMarker IS NULL THEN es_marker_version ELSE @esMarker END,
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
        cmd.Parameters.Add(new SqliteParameter("@esMarker",
            familySource == "system" ? 1 : (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@publishedAtUtc", now.ToString("o")));
        cmd.Parameters.Add(new SqliteParameter("@publishedBy", publishedBy ?? (object)DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Issue #249 (Phase 2): replace the per-type content-hash rows of one
    /// catalog version inside the caller's transaction. Always DELETEs the
    /// previous rows first (an overwrite invalidates them by definition);
    /// inserts the new set when <paramref name="entries"/> is non-null.
    /// A <c>null</c> set therefore means "unknown — pending backfill",
    /// which is exactly what the <c>type-hashes-v1</c> actualization task
    /// detects (no rows + <c>family_types</c> present).
    /// </summary>
    private static async Task ReplaceTypeHashesAsync(
        SqliteConnection connection,
        string versionId,
        IReadOnlyList<FamilyTypeHashEntry>? entries,
        DateTimeOffset now,
        CancellationToken ct)
    {
        using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.CommandText = "DELETE FROM family_type_hashes WHERE catalog_version_id = @versionId";
            deleteCmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
            await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (entries is null || entries.Count == 0)
        {
            return;
        }

        foreach (var entry in entries)
        {
            using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = """
                INSERT OR REPLACE INTO family_type_hashes
                    (catalog_version_id, type_identity_key, type_name, type_hash, created_at_utc)
                VALUES (@versionId, @identityKey, @typeName, @typeHash, @createdAtUtc)
                """;
            insertCmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
            insertCmd.Parameters.Add(new SqliteParameter("@identityKey", entry.TypeIdentityKey));
            insertCmd.Parameters.Add(new SqliteParameter("@typeName", entry.TypeName));
            insertCmd.Parameters.Add(new SqliteParameter("@typeHash", entry.HashHex));
            insertCmd.Parameters.Add(new SqliteParameter("@createdAtUtc", now.ToString("o")));
            await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Issue #249 (Phase 4): write the canonical content sections of one
    /// catalog version (JSON maps into <c>section_hashes</c> /
    /// <c>section_strings</c>) inside the caller's transaction. A
    /// <c>null</c> set CLEARS the columns — after an overwrite without a
    /// fresh snapshot the stale analytics must not be served (the
    /// <c>section-hashes-v1</c> task re-detects the version as pending).
    /// </summary>
    private static async Task WriteVersionSectionsAsync(
        SqliteConnection connection,
        string versionId,
        IReadOnlyList<ContentSectionHash>? sections,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE catalog_versions
            SET section_hashes = @hashes, section_strings = @strings
            WHERE id = @versionId
            """;
        cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
        if (sections is null)
        {
            cmd.Parameters.Add(new SqliteParameter("@hashes", DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@strings", DBNull.Value));
        }
        else
        {
            cmd.Parameters.Add(new SqliteParameter("@hashes", ContentSectionJsonSerializer.SerializeHashes(sections)));
            cmd.Parameters.Add(new SqliteParameter("@strings", ContentSectionJsonSerializer.SerializeStrings(sections)));
        }
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

}

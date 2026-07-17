using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed partial class LocalCatalogProvider
{
    public async Task<FamilyCatalogVersion?> GetVersionByIdAsync(
        string catalogItemId, string versionId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM catalog_versions
            WHERE catalog_item_id = @itemId AND id = @versionId
            LIMIT 1
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return ReadCatalogVersion(reader);
    }

    public async Task<FamilyCatalogVersion?> GetVersionByLabelAsync(
        string catalogItemId, string versionLabel, int targetRevitMajorVersion = 0, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();

        if (targetRevitMajorVersion > 0)
        {
            cmd.CommandText = """
                SELECT * FROM catalog_versions
                WHERE catalog_item_id = @itemId AND version_label = @label
                ORDER BY ABS(revit_major_version - @targetRevit) ASC, revit_major_version DESC
                LIMIT 1
                """;
            cmd.Parameters.Add(new SqliteParameter("@targetRevit", targetRevitMajorVersion));
        }
        else
        {
            cmd.CommandText = """
                SELECT * FROM catalog_versions
                WHERE catalog_item_id = @itemId AND version_label = @label
                ORDER BY revit_major_version DESC
                LIMIT 1
                """;
        }
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return ReadCatalogVersion(reader);
    }

    public async Task<SetActiveVersionResult> SetActiveVersionAsync(
        string catalogItemId, string versionLabel, CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("FMVersion",
            ("Method", nameof(SetActiveVersionAsync)),
            ("CatalogItemId", catalogItemId),
            ("VersionLabel", versionLabel));

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA foreign_keys = ON";
            await pragmaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using var tx = connection.BeginTransaction();
        try
        {
            // Step 1: Read previous current_version_label + name, the target
            // version's content_hash, and the target version's file name.
            // Issue #126: the catalog item name follows the ACTIVE version's
            // file name, so MakeActive onto a version stored under a
            // different file name renames the item accordingly.
            // If multiple Revit variants exist for the same label, pick the closest to current Revit (highest).
            string? previousLabel;
            string? previousName = null;
            string? versionContentHash = null;
            int? versionHashFormat = null;
            string? targetFileName = null;

            using (var readCmd = connection.CreateCommand())
            {
                readCmd.Transaction = tx;
                readCmd.CommandText = """
                    SELECT ci.current_version_label AS prev_label,
                           ci.name AS prev_name,
                           cv.content_hash AS target_hash,
                           cv.hash_format_version AS target_hash_fmt,
                           ff.file_name AS target_file_name
                    FROM catalog_items ci
                    LEFT JOIN catalog_versions cv
                        ON cv.catalog_item_id = ci.id
                       AND cv.version_label = @label
                    LEFT JOIN family_files ff
                        ON ff.id = cv.file_id
                    WHERE ci.id = @itemId
                    ORDER BY cv.revit_major_version DESC
                    LIMIT 1
                    """;
                readCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                readCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));

                using var reader = await readCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    tx.Rollback();
                    SmartConLogger.Warn($"catalog_items row not found for id={catalogItemId} [Action: verify catalogItemId is valid]");
                    return new SetActiveVersionResult(
                        Success: false, CatalogItemId: catalogItemId, VersionLabel: versionLabel,
                        PreviousVersionLabel: null, ActivatedAtUtc: DateTimeOffset.UtcNow,
                        ContentHashSynced: false,
                        ErrorMessage: $"catalog_items row not found for id={catalogItemId}");
                }

                previousLabel = reader.IsDBNull(reader.GetOrdinal("prev_label"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("prev_label"));
                previousName = reader.IsDBNull(reader.GetOrdinal("prev_name"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("prev_name"));
                versionContentHash = reader.IsDBNull(reader.GetOrdinal("target_hash"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("target_hash"));
                versionHashFormat = reader.IsDBNull(reader.GetOrdinal("target_hash_fmt"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("target_hash_fmt"));
                targetFileName = reader.IsDBNull(reader.GetOrdinal("target_file_name"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("target_file_name"));
            }

            // Check that at least one row in catalog_versions matched the label.
            if (versionContentHash is null && versionHashFormat is null)
            {
                // Distinguish "version label not found" from "found but both columns NULL".
                using var verifyCmd = connection.CreateCommand();
                verifyCmd.Transaction = tx;
                verifyCmd.CommandText = "SELECT COUNT(*) FROM catalog_versions WHERE catalog_item_id = @itemId AND version_label = @label";
                verifyCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                verifyCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                var countObj = await verifyCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                var count = countObj is long l ? (int)l : 0;
                if (count == 0)
                {
                    tx.Rollback();
                    SmartConLogger.Warn($"no catalog_versions row found for itemId={catalogItemId} label={versionLabel} [Action: make sure the version exists before activating]");
                    return new SetActiveVersionResult(
                        Success: false, CatalogItemId: catalogItemId, VersionLabel: versionLabel,
                        PreviousVersionLabel: previousLabel, ActivatedAtUtc: DateTimeOffset.UtcNow,
                        ContentHashSynced: false,
                        ErrorMessage: $"Version with label '{versionLabel}' not found for catalog item {catalogItemId}",
                        PreviousName: previousName);
                }
            }

            // Issue #126: resolve the item name from the activated version's
            // file name (without extension). When the version has no file
            // record the name is left untouched.
            var newName = !string.IsNullOrEmpty(targetFileName)
                ? Path.GetFileNameWithoutExtension(targetFileName)
                : null;
            var nameChanged = newName is not null
                && !string.Equals(newName, previousName, StringComparison.Ordinal);

            // Step 2: Atomically switch the pointer + sync content_hash (ADR-041 A.03)
            // + adopt the activated version's file name (Issue #126).
            using (var updCmd = connection.CreateCommand())
            {
                updCmd.Transaction = tx;
                updCmd.CommandText = """
                    UPDATE catalog_items
                    SET current_version_label = @label,
                        content_hash = @contentHash,
                        hash_format_version = @hashFmt,
                        name = COALESCE(@newName, name),
                        normalized_name = COALESCE(@newNormalizedName, normalized_name),
                        updated_at_utc = @now
                    WHERE id = @itemId
                    """;
                updCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                updCmd.Parameters.Add(new SqliteParameter("@contentHash", (object?)versionContentHash ?? DBNull.Value));
                updCmd.Parameters.Add(new SqliteParameter("@hashFmt", versionHashFormat.HasValue ? (object)versionHashFormat.Value : DBNull.Value));
                updCmd.Parameters.Add(new SqliteParameter("@newName",
                    nameChanged ? (object)newName! : DBNull.Value));
                updCmd.Parameters.Add(new SqliteParameter("@newNormalizedName",
                    nameChanged
                        ? (object)Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(newName!)
                        : DBNull.Value));
                updCmd.Parameters.Add(new SqliteParameter("@now", DateTimeOffset.UtcNow.ToString("o")));
                updCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));

                var rowsAffected = await updCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (rowsAffected == 0)
                {
                    tx.Rollback();
                    return new SetActiveVersionResult(
                        Success: false, CatalogItemId: catalogItemId, VersionLabel: versionLabel,
                        PreviousVersionLabel: previousLabel, ActivatedAtUtc: DateTimeOffset.UtcNow,
                        ContentHashSynced: false,
                        ErrorMessage: "catalog_items UPDATE affected 0 rows",
                        PreviousName: previousName);
                }
            }

            tx.Commit();
            var hashSynced = versionContentHash is not null;
            SmartConLogger.Info(
                $"switched active version: prev={(previousLabel ?? "<null>")} new={versionLabel} " +
                $"hashSynced={hashSynced} nameChanged={nameChanged}" +
                (nameChanged ? $" ('{previousName}' -> '{newName}')" : string.Empty));
            return new SetActiveVersionResult(
                Success: true, CatalogItemId: catalogItemId, VersionLabel: versionLabel,
                PreviousVersionLabel: previousLabel, ActivatedAtUtc: DateTimeOffset.UtcNow,
                ContentHashSynced: hashSynced,
                NameChanged: nameChanged,
                PreviousName: previousName,
                NewName: nameChanged ? newName : previousName);
        }
        catch (Exception)
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<DeleteVersionResult> DeleteVersionAsync(
        string catalogItemId, string versionLabel, CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("FMVersion",
            ("Method", nameof(DeleteVersionAsync)),
            ("CatalogItemId", catalogItemId),
            ("VersionLabel", versionLabel));

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA foreign_keys = ON";
            await pragmaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Step 1: Refuse to delete the active version (UC protection).
        string? currentLabel;
        using (var readCurrentCmd = connection.CreateCommand())
        {
            readCurrentCmd.CommandText = "SELECT current_version_label FROM catalog_items WHERE id = @itemId";
            readCurrentCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            var result = await readCurrentCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            currentLabel = result is null ? null : result.ToString();
        }

        if (string.Equals(currentLabel, versionLabel, StringComparison.Ordinal))
        {
            SmartConLogger.Warn($"refusing to delete active version {versionLabel} [Action: switch active version first via SetActiveVersionAsync]");
            return new DeleteVersionResult(
                Success: false, CatalogItemId: catalogItemId, VersionLabel: versionLabel,
                VersionsDeleted: 0, AssetsDeleted: 0, FilesDeleted: false,
                PhysicalDirectoryPath: null,
                ErrorMessage: "Cannot delete the active version. Switch active version first.");
        }

        // Step 2: Inside transaction — clean up dependent rows, then delete
        // catalog_versions (CASCADE removes family_types, extracted_attribute_values,
        // family_nested_shared_families via FK on version_id; but family_files and
        // family_data_import_runs need explicit cleanup because they don't have FK
        // CASCADE in the "version_id → catalog_versions" direction:
        //   family_files is referenced BY catalog_versions.file_id (NOT the reverse),
        //   family_data_import_runs.version_id is a soft pointer without FK at all).
        int versionsDeleted;
        int assetsDeleted;
        int dbFilesDeleted = 0;
        int runsCleared;

        using var tx = connection.BeginTransaction();
        try
        {
            // family_assets are bound by (catalog_item_id, version_label) — no FK
            // to catalog_versions; cleanup is explicit.
            using (var assetsCmd = connection.CreateCommand())
            {
                assetsCmd.Transaction = tx;
                assetsCmd.CommandText = "DELETE FROM family_assets WHERE catalog_item_id = @itemId AND version_label = @label";
                assetsCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                assetsCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                assetsDeleted = await assetsCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // family_files: catalog_versions.file_id references family_files.id (FK
            // ON DELETE CASCADE in that direction). If we DELETE family_files first,
            // the CASCADE would remove catalog_versions rows BEFORE our explicit
            // catalog_versions DELETE — leaving 0 rows affected and breaking the
            // versionsDeleted count. Solution: capture file_ids into memory first,
            // then DELETE catalog_versions (CASCADE removes types/values/nested-shared),
            // then DELETE family_files explicitly using the captured IDs.
            var capturedFileIds = new List<string>();
            using (var readFilesCmd = connection.CreateCommand())
            {
                readFilesCmd.Transaction = tx;
                readFilesCmd.CommandText = """
                    SELECT file_id FROM catalog_versions
                    WHERE catalog_item_id = @itemId AND version_label = @label
                    """;
                readFilesCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                readFilesCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                using var filesReader = await readFilesCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await filesReader.ReadAsync(ct).ConfigureAwait(false))
                {
                    capturedFileIds.Add(filesReader.GetString(0));
                }
            }

            // family_data_import_runs.version_id is a soft pointer (no FK);
            // null it out so audit rows survive but the dangling pointer is gone.
            using (var runsCmd = connection.CreateCommand())
            {
                runsCmd.Transaction = tx;
                runsCmd.CommandText = """
                    UPDATE family_data_import_runs
                    SET version_id = NULL
                    WHERE version_id IN (
                        SELECT id FROM catalog_versions
                        WHERE catalog_item_id = @itemId AND version_label = @label
                    )
                    """;
                runsCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                runsCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                runsCleared = await runsCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // catalog_versions DELETE triggers FK CASCADE on:
            //   - family_types.version_id (added in V17) → CASCADE
            //   - extracted_attribute_values.version_id (added in V17) → CASCADE
            //   - family_nested_shared_families.version_id (always present) → CASCADE
            using (var verCmd = connection.CreateCommand())
            {
                verCmd.Transaction = tx;
                verCmd.CommandText = "DELETE FROM catalog_versions WHERE catalog_item_id = @itemId AND version_label = @label";
                verCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                verCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                versionsDeleted = await verCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Now safe to delete family_files rows — catalog_versions rows that
            // referenced them are gone, so no FK enforcement is violated.
            foreach (var fileId in capturedFileIds)
            {
                using var filesCmd = connection.CreateCommand();
                filesCmd.Transaction = tx;
                filesCmd.CommandText = "DELETE FROM family_files WHERE id = @fileId";
                filesCmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
                dbFilesDeleted += await filesCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        SmartConLogger.Info(
            $"deleted DB rows: versions={versionsDeleted} assets={assetsDeleted} " +
            $"files={dbFilesDeleted} importRunsCleared={runsCleared}");

        // Step 3: Filesystem cleanup — best effort, after DB commit (so we never leave orphan files on rollback).
        var dbRoot = _database.GetDatabaseRoot();
        var versionDir = Path.Combine(dbRoot, "files", catalogItemId, versionLabel);
        var filesDeleted = false;

        if (Directory.Exists(versionDir))
        {
            try
            {
                await Task.Run(() =>
                {
                    RemoveReadOnlyAttributes(versionDir);
                    Directory.Delete(versionDir, recursive: true);
                }, ct).ConfigureAwait(false);
                filesDeleted = true;
                SmartConLogger.Info($"deleted physical files at {Path.GetFileName(versionDir)}");
            }
            catch (Exception ex)
            {
                // L8: message uses only the directory leaf name (versionLabel).
                // Full path PII is avoided; the scope already contains
                // CatalogItemId + VersionLabel for correlation.
                var leafName = !string.IsNullOrEmpty(versionDir)
                    ? Path.GetFileName(versionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    : "<unknown>";
                SmartConLogger.Warn($"failed to delete physical files for version '{leafName}': {ex.Message} [Action: close any Revit document using this family and retry DeleteVersion — DB rows are already removed]");
                // Not a failure — DB is consistent; orphan dir will be cleaned on next retry.
            }
        }

        return new DeleteVersionResult(
            Success: true, CatalogItemId: catalogItemId, VersionLabel: versionLabel,
            VersionsDeleted: versionsDeleted, AssetsDeleted: assetsDeleted,
            FilesDeleted: filesDeleted, PhysicalDirectoryPath: filesDeleted ? null : versionDir);
    }
}

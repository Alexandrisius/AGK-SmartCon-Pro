using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed partial class LocalCatalogProvider
{
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
        // #249 (Phase 5): CAS pool paths referenced by this label's assets
        // (captured inside the tx, cleaned after the commit).
        var capturedPoolPaths = new List<string>();

        using var tx = connection.BeginTransaction();
        try
        {
            // #249 (Phase 5): capture the CAS pool paths referenced by this
            // label's assets BEFORE the delete — after commit, each pooled
            // file is deleted only when no other version/family references
            // it anymore (refcount, Plan v3).
            using (var poolCmd = connection.CreateCommand())
            {
                poolCmd.Transaction = tx;
                poolCmd.CommandText = """
                    SELECT relative_path FROM family_assets
                    WHERE catalog_item_id = @itemId AND version_label = @label
                      AND relative_path LIKE @poolPrefix
                    """;
                poolCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                poolCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                poolCmd.Parameters.Add(new SqliteParameter("@poolPrefix",
                    StoragePathResolver.SharedPreviewPoolRelativePrefix + "%"));
                using var poolReader = await poolCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await poolReader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (!poolReader.IsDBNull(0))
                    {
                        capturedPoolPaths.Add(poolReader.GetString(0));
                    }
                }
            }

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

        // #249 (Phase 5): pooled preview files shared with other
        // versions/families are deleted only when the last reference is
        // gone — AFTER the DB commit (rollback can never orphan a
        // reference).
        foreach (var poolPath in capturedPoolPaths)
        {
            await SharedPreviewPoolCleanup.DeletePoolFileIfOrphanedAsync(_database, poolPath, ct)
                .ConfigureAwait(false);
        }

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

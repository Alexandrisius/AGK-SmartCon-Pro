using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

public sealed partial class LocalCatalogMigrator
{
    /// <summary>
    /// v2.0.0 (ADR-036) migration v15: add FOREIGN KEY (type_id) → family_types(id)
    /// ON DELETE CASCADE on extracted_attribute_values. Recreates the table to
    /// add the constraint (SQLite limitation). Pre-existing orphan rows are
    /// cleaned up inside the SQL constant.
    /// </summary>
    private static async Task MigrateV15Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 15) return;

        using var tx = connection.BeginTransaction();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV15AddAttributeValuesForeignKey;
            await cmd.ExecuteNonQueryAsync(ct);

            using var versionCmd = connection.CreateCommand();
            versionCmd.CommandText = "UPDATE schema_info SET value = '15' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v15: added FK on extracted_attribute_values.type_id (ON DELETE CASCADE)");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// v2.1.0 migration v16: add content_hash and hash_format_version
    /// columns to catalog_items and catalog_versions for content-fingerprint
    /// deduplication. Additive only — no breaking changes. Idempotent:
    /// each ALTER TABLE is guarded by a column-existence check.
    /// </summary>
    private static async Task MigrateV16Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 16) return;

        using var tx = connection.BeginTransaction();
        try
        {
            if (!await ColumnExistsAsync(connection, "catalog_items", "content_hash", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE catalog_items ADD COLUMN content_hash TEXT";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (!await ColumnExistsAsync(connection, "catalog_items", "hash_format_version", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE catalog_items ADD COLUMN hash_format_version INTEGER";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (!await ColumnExistsAsync(connection, "catalog_versions", "content_hash", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE catalog_versions ADD COLUMN content_hash TEXT";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (!await ColumnExistsAsync(connection, "catalog_versions", "hash_format_version", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE catalog_versions ADD COLUMN hash_format_version INTEGER";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = FamilyCatalogSql.CreateV16Indexes;
            await idxCmd.ExecuteNonQueryAsync(ct);

            using var versionCmd = connection.CreateCommand();
            versionCmd.CommandText = "UPDATE schema_info SET value = '16' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v16: added content_hash columns and indexes");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// v2.0.0 (ADR-041) migration v17: add FOREIGN KEY (version_id) REFERENCES
    /// catalog_versions(id) ON DELETE CASCADE on family_types and
    /// extracted_attribute_values. Also adds the composite index
    /// (catalog_item_id, version_label) on catalog_versions for fast
    /// GetVersionByLabelAsync / SetActiveVersionAsync lookups.
    ///
    /// Recreate-and-copy pattern (same as V15) because SQLite does not support
    /// ALTER TABLE ADD CONSTRAINT. Each table is recreated inside a single
    /// transaction with orphan-row cleanup before the copy.
    ///
    /// Order matters: family_types is recreated first because
    /// extracted_attribute_values has FK (type_id) → family_types(id).
    /// </summary>
    private static async Task MigrateV17Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 17) return;

        using var tx = connection.BeginTransaction();
        try
        {
            // Step 1: family_types — recreate with FK (version_id) ON DELETE CASCADE
            using (var ftCmd = connection.CreateCommand())
            {
                ftCmd.Transaction = tx;
                ftCmd.CommandText = FamilyCatalogSql.MigrateV17RecreateFamilyTypesWithVersionFk;
                await ftCmd.ExecuteNonQueryAsync(ct);
            }

            // Step 2: extracted_attribute_values — recreate with FK (version_id) ON DELETE CASCADE
            using (var eavCmd = connection.CreateCommand())
            {
                eavCmd.Transaction = tx;
                eavCmd.CommandText = FamilyCatalogSql.MigrateV17RecreateExtractedAttributeValuesWithVersionFk;
                await eavCmd.ExecuteNonQueryAsync(ct);
            }

            // Step 3: composite index on catalog_versions for ByLabel lookups
            using (var idxCmd = connection.CreateCommand())
            {
                idxCmd.Transaction = tx;
                idxCmd.CommandText = FamilyCatalogSql.CreateV17Indexes;
                await idxCmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '17' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v17: added FK on family_types.version_id and extracted_attribute_values.version_id (ON DELETE CASCADE)");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// v2.1.0 (ADR-041 rev #2) migration v18: change the UNIQUE constraint
    /// on family_types from (catalog_item_id, type_name) to
    /// (catalog_item_id, version_id, type_name) so a type name can coexist
    /// across multiple versions of the same catalog item. This is the
    /// prerequisite for per-version type storage and version-scoped DELETE
    /// in SyncTypesAsync: without it, INSERT for a new version with the
    /// same type name as an existing version hits ON CONFLICT and silently
    /// reassigns family_types.version_id to the new version, destroying the
    /// previous version's type rows.
    ///
    /// Recreate-and-copy pattern (same as V15/V17). Idempotent: if the
    /// table already has the new UNIQUE, the recreate is a no-op data copy.
    /// </summary>
    private static async Task MigrateV18Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 18) return;

        using var tx = connection.BeginTransaction();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = FamilyCatalogSql.MigrateV18RecreateFamilyTypesPerVersionUnique;
            await cmd.ExecuteNonQueryAsync(ct);

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '18' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v18: changed family_types UNIQUE to (catalog_item_id, version_id, type_name) for per-version type storage");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V19: adds <c>published_by TEXT</c> column to <c>catalog_versions</c>
    /// to track which Revit user published each version. Simple ALTER TABLE
    /// ADD COLUMN — no recreate needed. ADR-041 rev #5.
    /// </summary>
    private static async Task MigrateV19Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 19) return;

        using var tx = connection.BeginTransaction();
        try
        {
            if (!await ColumnExistsAsync(connection, "catalog_versions", "published_by", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.MigrateV19AddPublishedByColumn;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '19' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v19: added published_by column to catalog_versions for per-version author tracking");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V20 (ADR-045 / #119): adds <c>base_type INTEGER NOT NULL DEFAULT 0</c>
    /// column to <c>database_meta</c>. <c>0</c> = General (default for
    /// existing databases that predate #119), <c>1</c> = Project. The base
    /// type itself lives in <c>registry.json</c> as the source of truth
    /// (decision A1); this column is a convenience cache for RBAC and other
    /// consumers that read the catalog without touching the registry. Simple
    /// ALTER TABLE ADD COLUMN — no recreate needed. Existing rows get 0
    /// (General) on account of the DEFAULT clause.
    /// </summary>
    private static async Task MigrateV20Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 20) return;

        using var tx = connection.BeginTransaction();
        try
        {
            if (!await ColumnExistsAsync(connection, "database_meta", "base_type", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.MigrateV20AddBaseTypeColumn;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '20' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v20: added base_type column to database_meta (General=0 default) — project-base binding cache for #119");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V21 (#119): adds <c>project_binding_json</c> column to <c>database_meta</c>.
    /// Project base binding is persisted inside the catalog database so that
    /// disconnecting and later reconnecting a project database restores its
    /// <see cref="BaseType.Project"/> kind and binding rules.
    /// </summary>
    private static async Task MigrateV21Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 21) return;

        using var tx = connection.BeginTransaction();
        try
        {
            if (!await ColumnExistsAsync(connection, "database_meta", "project_binding_json", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.MigrateV21AddProjectBindingColumn;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '21' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v21: added project_binding_json column to database_meta — binding survives disconnect/reconnect");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V22 (ADR-055): family-facts subsystem — adds
    /// <c>catalog_items.revit_category_id INTEGER</c> (BuiltInCategory
    /// ordinal for rule-registry matching) and the <c>family_facts</c>
    /// table (one row per catalog item + fact). Additive only; the rows
    /// are backfilled by the <c>family-facts-v1</c> actualization task.
    /// </summary>
    private static async Task MigrateV22Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 22) return;

        using var tx = connection.BeginTransaction();
        try
        {
            if (!await ColumnExistsAsync(connection, "catalog_items", "revit_category_id", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.MigrateV22AddRevitCategoryIdColumn;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (!await TableExistsAsync(connection, "family_facts", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.CreateFamilyFacts;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using (var idxCmd = connection.CreateCommand())
            {
                idxCmd.Transaction = tx;
                idxCmd.CommandText = FamilyCatalogSql.CreateFamilyFactsIndexes;
                await idxCmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '22' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v22: added revit_category_id column and family_facts table (family-facts subsystem, ADR-055)");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}

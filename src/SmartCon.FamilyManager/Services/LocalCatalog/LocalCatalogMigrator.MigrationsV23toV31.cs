using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

public sealed partial class LocalCatalogMigrator
{
    /// <summary>
    /// V23 (#157): adds <c>catalog_versions.glb_state INTEGER</c> — terminal
    /// "no extractable geometry" marker for the glb-v1 task (-1). Without
    /// it, families that legitimately have no 3D (2D/annotation symbols)
    /// stayed pending forever (eternal amber dot).
    /// </summary>
    private static async Task MigrateV23Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 23) return;

        using var tx = connection.BeginTransaction();
        try
        {
            if (!await ColumnExistsAsync(connection, "catalog_versions", "glb_state", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.MigrateV23AddGlbStateColumn;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '23' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v23: added glb_state column to catalog_versions (terminal no-geometry marker for glb-v1, #157)");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V24 (ADR-058, #173): adds <c>database_meta.min_plugin_version</c> —
    /// the forward-compatibility floor — and retro-gates databases already
    /// actualized to FHV3 (<c>hash_format_version = 3</c>) to
    /// <c>2.0.1-beta.5</c>, the first FHV3-capable release.
    /// </summary>
    private static async Task MigrateV24Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 24) return;

        using var tx = connection.BeginTransaction();
        try
        {
            if (!await ColumnExistsAsync(connection, "database_meta", "min_plugin_version", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.MigrateV24AddMinPluginVersionColumn;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using (var backfillCmd = connection.CreateCommand())
            {
                backfillCmd.Transaction = tx;
                backfillCmd.CommandText = FamilyCatalogSql.MigrateV24BackfillMinPluginVersion;
                var gated = await backfillCmd.ExecuteNonQueryAsync(ct);
                if (gated > 0)
                {
                    SmartConLogger.Info(
                        "Migration v24: database carries FHV3 hashes — min_plugin_version set to 2.0.1-beta.5 " +
                        "(older plugins will connect read-only, ADR-058)");
                }
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '24' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v24: added min_plugin_version column to database_meta (plugin forward-compatibility gate, ADR-058, #173)");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V25 (import validation gate): adds the <c>category_validation_rules</c>
    /// table — per-binding validation rules (HasValue / Equals / Between / …)
    /// checked on import and on category change. Additive only; the table is
    /// empty until the user defines rules in the category editor.
    /// </summary>
    private static async Task MigrateV25Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 25) return;

        using var tx = connection.BeginTransaction();
        try
        {
            if (!await TableExistsAsync(connection, "category_validation_rules", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.CreateCategoryValidationRules;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using (var idxCmd = connection.CreateCommand())
            {
                idxCmd.Transaction = tx;
                idxCmd.CommandText = FamilyCatalogSql.CreateCategoryValidationRulesIndexes;
                await idxCmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '25' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v25: added category_validation_rules table (import validation gate)");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V26 (#183): adds <c>family_name TEXT NOT NULL DEFAULT ''</c> to
    /// <c>family_types</c> and extends the UNIQUE identity to
    /// (catalog_item_id, version_id, family_name, type_name) — a system type
    /// is identified by (family, name), never by name alone ("Стандарт"
    /// exists in both "Conduit with Fittings" and "Conduit without
    /// Fittings"). Recreate-and-copy pattern (same as V15/V17/V18).
    /// Idempotent: if the table already has family_name, the recreate is a
    /// no-op data copy.
    /// </summary>
    private static async Task MigrateV26Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 26) return;

        using var tx = connection.BeginTransaction();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = FamilyCatalogSql.MigrateV26RecreateFamilyTypesWithFamilyName;
            await cmd.ExecuteNonQueryAsync(ct);

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '26' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v26: added family_name to family_types; UNIQUE is now (catalog_item_id, version_id, family_name, type_name) (#183)");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V27 (#190, ADR-064): adds <c>family_key TEXT NOT NULL DEFAULT ''</c>
    /// to <c>family_types</c> — the locale-invariant system family identity.
    /// Plain ADD COLUMN (no recreate): the key is not a UNIQUE member.
    /// Guarded by ColumnExists so a partially applied V27 heals on restart.
    /// </summary>
    private static async Task MigrateV27Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 27) return;

        using var tx = connection.BeginTransaction();
        try
        {
            var columnAdded = false;
            if (!await ColumnExistsAsync(connection, "family_types", "family_key", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.MigrateV27AddFamilyKeyColumn;
                await cmd.ExecuteNonQueryAsync(ct);
                columnAdded = true;
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '27' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            if (columnAdded)
            {
                SmartConLogger.Info("Migration v27: added family_key to family_types — locale-invariant system family identity (#190)");
            }
            else
            {
                // Fresh DB: the column already exists via CREATE TABLE —
                // only the version bump was needed.
                SmartConLogger.Debug("Migration v27: family_key already present (fresh schema) — version bumped to 27");
            }
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static async Task MigrateV28Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 28) return;

        using var tx = connection.BeginTransaction();
        try
        {
            var columnAdded = false;
            if (!await ColumnExistsAsync(connection, "catalog_versions", "es_marker_version", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.MigrateV28AddEsMarkerVersionColumn;
                await cmd.ExecuteNonQueryAsync(ct);
                columnAdded = true;
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '28' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            if (columnAdded)
            {
                SmartConLogger.Info(
                    "Migration v28: added es_marker_version to catalog_versions — " +
                    "existing staged versions become pending for mini-project-marker-v1 (#189)");
            }
            else
            {
                SmartConLogger.Debug("Migration v28: es_marker_version already present (fresh schema) — version bumped to 28");
            }
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static async Task MigrateV29Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 29) return;

        using var tx = connection.BeginTransaction();
        try
        {
            var tableCreated = false;
            if (!await TableExistsAsync(connection, "family_dependencies", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.CreateFamilyDependencies;
                await cmd.ExecuteNonQueryAsync(ct);
                tableCreated = true;
            }

            using (var idxCmd = connection.CreateCommand())
            {
                idxCmd.Transaction = tx;
                idxCmd.CommandText = FamilyCatalogSql.CreateFamilyDependenciesIndexes;
                await idxCmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '29' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            if (tableCreated)
            {
                SmartConLogger.Info(
                    "Migration v29: added family_dependencies table — parent→child dependency links (#207, ADR-066)");
            }
            else
            {
                SmartConLogger.Debug("Migration v29: family_dependencies already present (fresh schema) — version bumped to 29");
            }
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static async Task MigrateV30Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 30) return;

        using var tx = connection.BeginTransaction();
        try
        {
            var columnAdded = false;
            if (!await ColumnExistsAsync(connection, "family_dependencies", "child_version_label", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "ALTER TABLE family_dependencies ADD COLUMN child_version_label TEXT";
                await cmd.ExecuteNonQueryAsync(ct);
                columnAdded = true;
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '30' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info(columnAdded
                ? "Migration v30: family_dependencies.child_version_label — embedded child version for dependency drift detection (E2, #209)"
                : "Migration v30: child_version_label already present — version bumped to 30");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V31 (#241): adds the auto-assignment rule tables
    /// (<c>category_assignment_rule_groups</c> + conditions with the
    /// attribute/system CHECK constraint). Plain CREATE IF NOT EXISTS —
    /// no data rewrite.
    /// </summary>
    private static async Task MigrateV31Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 31) return;

        using var tx = connection.BeginTransaction();
        try
        {
            var tablesCreated = false;
            if (!await TableExistsAsync(connection, "category_assignment_rule_groups", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.CreateCategoryAssignmentRuleGroups;
                await cmd.ExecuteNonQueryAsync(ct);
                tablesCreated = true;
            }

            if (!await TableExistsAsync(connection, "category_assignment_conditions", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.CreateCategoryAssignmentConditions;
                await cmd.ExecuteNonQueryAsync(ct);
                tablesCreated = true;
            }

            using (var idxCmd = connection.CreateCommand())
            {
                idxCmd.Transaction = tx;
                idxCmd.CommandText = FamilyCatalogSql.CreateCategoryAssignmentRuleIndexes;
                await idxCmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '31' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            if (tablesCreated)
            {
                SmartConLogger.Info("Migration v31: added category_assignment_rule_groups + category_assignment_conditions (auto-assignment, #241)");
            }
            else
            {
                SmartConLogger.Debug("Migration v31: assignment tables already present (fresh schema) — version bumped to 31");
            }
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}

using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

public sealed partial class LocalCatalogMigrator
{
    /// <summary>
    /// V32 (#249, Phase 2): adds <c>family_type_hashes</c> — per-type
    /// content hashes of a catalog version (per-type stale detection for
    /// loadable families, content-grade #179 for system, cross-family
    /// type dedup for the future cloud). Plain CREATE IF NOT EXISTS +
    /// indexes — no data rewrite; legacy versions are backfilled by the
    /// optional <c>type-hashes-v1</c> actualization task.
    /// </summary>
    private static async Task MigrateV32Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 32) return;

        using var tx = connection.BeginTransaction();
        try
        {
            var tableCreated = false;
            if (!await TableExistsAsync(connection, "family_type_hashes", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.CreateFamilyTypeHashes;
                await cmd.ExecuteNonQueryAsync(ct);
                tableCreated = true;
            }

            using (var idxCmd = connection.CreateCommand())
            {
                idxCmd.Transaction = tx;
                idxCmd.CommandText = FamilyCatalogSql.CreateFamilyTypeHashesIndexes;
                await idxCmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '32' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            if (tableCreated)
            {
                SmartConLogger.Info("Migration v32: added family_type_hashes (per-type content hashes, #249)");
            }
            else
            {
                SmartConLogger.Debug("Migration v32: family_type_hashes already present (fresh schema) — version bumped to 32");
            }
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V33 (#249, Phase 4): adds <c>catalog_versions.section_hashes</c>
    /// and <c>section_strings</c> — the canonical content sections as two
    /// flat JSON maps for the batch dialog's "what changed" diff. Plain
    /// ADD COLUMN — no data rewrite; legacy versions are backfilled by
    /// the optional <c>section-hashes-v1</c> actualization task.
    /// </summary>
    private static async Task MigrateV33Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 33) return;

        using var tx = connection.BeginTransaction();
        try
        {
            var columnsAdded = false;
            if (!await ColumnExistsAsync(connection, "catalog_versions", "section_hashes", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "ALTER TABLE catalog_versions ADD COLUMN section_hashes TEXT";
                await cmd.ExecuteNonQueryAsync(ct);
                columnsAdded = true;
            }
            if (!await ColumnExistsAsync(connection, "catalog_versions", "section_strings", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "ALTER TABLE catalog_versions ADD COLUMN section_strings TEXT";
                await cmd.ExecuteNonQueryAsync(ct);
                columnsAdded = true;
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '33' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info(columnsAdded
                ? "Migration v33: catalog_versions +section_hashes/+section_strings (content-section analytics, #249)"
                : "Migration v33: section columns already present — version bumped to 33");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V34 (#254, ADR-072): routing rules of system MEPCurve types as
    /// catalog data (<c>family_routing_rules</c> +
    /// <c>family_routing_type_settings</c>) — additive table creation,
    /// no data rewrite; pre-V34 versions are served by the sync
    /// legacy-fallback (read routing from the mini-project) until the
    /// optional backfill actualization fills the tables.
    /// </summary>
    private static async Task MigrateV34Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 34) return;

        using var tx = connection.BeginTransaction();
        try
        {
            var created = false;
            if (!await TableExistsAsync(connection, "family_routing_rules", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.CreateFamilyRoutingRules;
                await cmd.ExecuteNonQueryAsync(ct);
                cmd.CommandText = FamilyCatalogSql.CreateFamilyRoutingRulesIndexes;
                await cmd.ExecuteNonQueryAsync(ct);
                created = true;
            }
            if (!await TableExistsAsync(connection, "family_routing_type_settings", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.CreateFamilyRoutingTypeSettings;
                await cmd.ExecuteNonQueryAsync(ct);
                created = true;
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '34' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info(created
                ? "Migration v34: added family_routing_rules + family_routing_type_settings — routing as catalog data (#254, ADR-072)"
                : "Migration v34: routing tables already present (fresh schema) — version bumped to 34");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V35 (#254, ADR-072 Phase 2b): adds
    /// <c>catalog_versions.routing_backfilled</c> — the tracking column of
    /// the routing backfill/slimming actualization (0 = pending).
    /// </summary>
    private static async Task MigrateV35Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 35) return;

        using var tx = connection.BeginTransaction();
        try
        {
            var columnAdded = false;
            if (!await ColumnExistsAsync(connection, "catalog_versions", "routing_backfilled", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.MigrateV35AddRoutingBackfilled;
                await cmd.ExecuteNonQueryAsync(ct);
                columnAdded = true;
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '35' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info(columnAdded
                ? "Migration v35: catalog_versions +routing_backfilled (routing backfill/slimming tracking, #254)"
                : "Migration v35: routing_backfilled already present — version bumped to 35");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V38 (FHV21): per-version segment routing rules — the segment
    /// configuration leaves the item-level routing channel and becomes
    /// versioned mini-project content. Additive table; legacy versions are
    /// backfilled by the segment-rules-v1 actualization from their minis.
    /// </summary>
    private static async Task MigrateV38Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 38) return;

        using var tx = connection.BeginTransaction();
        try
        {
            var tableAdded = false;
            using (var check = connection.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'family_segment_rules'";
                tableAdded = await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is null;
            }

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = FamilyCatalogSql.MigrateV38AddSegmentRules;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '38' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            tx.Commit();
            SmartConLogger.Info(tableAdded
                ? "Migration v38: family_segment_rules (FHV21 per-version segment configuration)"
                : "Migration v38: segment rules table already present — version bumped to 38");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V37 (ADR-072 World B): item-level routing link tables + copy of the
    /// current version's V34 rows (curated links survive the upgrade).
    /// </summary>
    private static async Task MigrateV37Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 37) return;

        using var tx = connection.BeginTransaction();
        try
        {
            var tableAdded = false;
            using (var check = connection.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'item_routing_rules'";
                tableAdded = await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is null;
            }

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = FamilyCatalogSql.MigrateV37AddItemRoutingTables;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '37' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            tx.Commit();
            SmartConLogger.Info(tableAdded
                ? "Migration v37: item_routing_rules/item_routing_type_settings (ADR-072 World B routing links)"
                : "Migration v37: item routing tables already present — version bumped to 37");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V36 (ADR-072, Phase 3): per-version segment size tables — the routing
    /// editor's min/max dropdown source (nominal diameters, as in the Revit
    /// routing dialog). Additive analytics-style table: legacy versions are
    /// backfilled by the optional segment-sizes-v1 actualization, import
    /// writes it for new versions.
    /// </summary>
    private static async Task MigrateV36Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 36) return;

        using var tx = connection.BeginTransaction();
        try
        {
            var tableAdded = false;
            using (var check = connection.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'family_segment_sizes'";
                tableAdded = await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is null;
            }
            if (tableAdded)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.MigrateV36AddSegmentSizes;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '36' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            tx.Commit();
            SmartConLogger.Info(tableAdded
                ? "Migration v36: family_segment_sizes table (routing editor size dropdowns, ADR-072 Phase 3)"
                : "Migration v36: family_segment_sizes already present — version bumped to 36");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}

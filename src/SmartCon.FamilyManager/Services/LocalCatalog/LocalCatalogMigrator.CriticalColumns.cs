using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

public sealed partial class LocalCatalogMigrator
{
    private static async Task EnsureCriticalColumnsAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await ColumnExistsAsync(connection, "family_assets", "is_primary", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV5AddIsPrimaryColumn;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "attribute_definitions", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateAttributeDefinitions;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        else if (!await ColumnExistsAsync(connection, "attribute_definitions", "group_name", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE attribute_definitions ADD COLUMN group_name TEXT";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "category_attribute_bindings", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateCategoryAttributeBindings;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "family_data_import_runs", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateFamilyDataImportRuns;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "extracted_attribute_values", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateExtractedAttributeValues;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        else if (await IsColumnNotNullAsync(connection, "extracted_attribute_values", "attribute_id", ct))
        {
            using var recreateCmd = connection.CreateCommand();
            recreateCmd.CommandText = FamilyCatalogSql.MigrateV8RecreateExtractedAttributeValues;
            await recreateCmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "family_types", "version_id", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV6FamilyTypesAddColumns;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "db_users", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateDbUsers;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "database_meta", "owner_identity", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV7AddOwnerIdentity;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "database_meta", "base_type", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV20AddBaseTypeColumn;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "database_meta", "min_plugin_version", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV24AddMinPluginVersionColumn;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "catalog_items", "family_source", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE catalog_items ADD COLUMN family_source TEXT NOT NULL DEFAULT 'loadable'";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "catalog_items", "revit_category", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE catalog_items ADD COLUMN revit_category TEXT";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "catalog_items", "revit_category_id", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV22AddRevitCategoryIdColumn;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "family_facts", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateFamilyFacts;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using (var factsIdxCmd = connection.CreateCommand())
        {
            factsIdxCmd.CommandText = FamilyCatalogSql.CreateFamilyFactsIndexes;
            await factsIdxCmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "family_types", "type_unique_id", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE family_types ADD COLUMN type_unique_id TEXT";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "family_types", "family_key", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV27AddFamilyKeyColumn;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "catalog_versions", "es_marker_version", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV28AddEsMarkerVersionColumn;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "family_nested_shared_families", ct))
        {
            using var createCmd = connection.CreateCommand();
            createCmd.CommandText = FamilyCatalogSql.CreateFamilyNestedSharedFamilies;
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        // Indexes are idempotent (CREATE INDEX IF NOT EXISTS). Always attempt
        // them so a partial migration (table created, indexes failed) is
        // healed on next launch.
        using (var idxCmd = connection.CreateCommand())
        {
            idxCmd.CommandText = FamilyCatalogSql.CreateNestedSharedFamiliesIndexes;
            await idxCmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "family_dependencies", ct))
        {
            using var createCmd = connection.CreateCommand();
            createCmd.CommandText = FamilyCatalogSql.CreateFamilyDependencies;
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        // Indexes are idempotent (CREATE INDEX IF NOT EXISTS) — same healing
        // rationale as the nested-shared indexes above.
        using (var depIdxCmd = connection.CreateCommand())
        {
            depIdxCmd.CommandText = FamilyCatalogSql.CreateFamilyDependenciesIndexes;
            await depIdxCmd.ExecuteNonQueryAsync(ct);
        }

        // v16 content_hash columns — ensure they exist even if migration
        // sequence was interrupted.
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

        if (!await ColumnExistsAsync(connection, "catalog_versions", "glb_state", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV23AddGlbStateColumn;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "category_validation_rules", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateCategoryValidationRules;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using (var rulesIdxCmd = connection.CreateCommand())
        {
            rulesIdxCmd.CommandText = FamilyCatalogSql.CreateCategoryValidationRulesIndexes;
            await rulesIdxCmd.ExecuteNonQueryAsync(ct);
        }

        using (var v16IdxCmd = connection.CreateCommand())
        {
            v16IdxCmd.CommandText = FamilyCatalogSql.CreateV16Indexes;
            await v16IdxCmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "family_type_hashes", ct))
        {
            using var createCmd = connection.CreateCommand();
            createCmd.CommandText = FamilyCatalogSql.CreateFamilyTypeHashes;
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        // Indexes are idempotent (CREATE INDEX IF NOT EXISTS) — same healing
        // rationale as the nested-shared indexes above.
        using (var typeHashIdxCmd = connection.CreateCommand())
        {
            typeHashIdxCmd.CommandText = FamilyCatalogSql.CreateFamilyTypeHashesIndexes;
            await typeHashIdxCmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "catalog_versions", "section_hashes", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE catalog_versions ADD COLUMN section_hashes TEXT";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "catalog_versions", "section_strings", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE catalog_versions ADD COLUMN section_strings TEXT";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}

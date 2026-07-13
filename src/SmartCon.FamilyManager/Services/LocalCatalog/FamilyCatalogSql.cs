namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal static class FamilyCatalogSql
{
    public const string CreateDatabaseMeta = """
        CREATE TABLE IF NOT EXISTS database_meta (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            description TEXT,
            created_at_utc TEXT NOT NULL,
            schema_version INTEGER NOT NULL DEFAULT 2
        )
        """;

    public const string CreateSchemaInfo = """
        CREATE TABLE IF NOT EXISTS schema_info (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        )
        """;

    // The `manufacturer` column is kept for backward compatibility but is no
    // longer surfaced in the UI. The "Производитель" field was removed from the
    // Family Properties dialog because category parameter schemas already
    // expose "Изготовитель" on the ATTRIBUTES tab when present. No DB migration
    // is performed — the column stays nullable and is simply not written by
    // UpdateItemAsync. See #109 for rationale.
    public const string CreateCatalogItems = """
        CREATE TABLE IF NOT EXISTS catalog_items (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            normalized_name TEXT NOT NULL,
            description TEXT,
            category_name TEXT,
            category_id TEXT,
            manufacturer TEXT,
            content_status TEXT NOT NULL DEFAULT 'Active',
            current_version_label TEXT,
            published_by TEXT,
            family_source TEXT NOT NULL DEFAULT 'loadable',
            revit_category TEXT,
            content_hash TEXT,
            hash_format_version INTEGER,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        )
        """;

    public const string CreateCatalogVersions = """
        CREATE TABLE IF NOT EXISTS catalog_versions (
            id TEXT PRIMARY KEY,
            catalog_item_id TEXT NOT NULL,
            file_id TEXT NOT NULL,
            version_label TEXT NOT NULL,
            revit_major_version INTEGER NOT NULL,
            types_count INTEGER,
            parameters_count INTEGER,
            content_hash TEXT,
            hash_format_version INTEGER,
            published_at_utc TEXT NOT NULL,
            published_by TEXT,
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
            FOREIGN KEY (file_id) REFERENCES family_files(id) ON DELETE CASCADE,
            UNIQUE(catalog_item_id, version_label, revit_major_version)
        )
        """;

    public const string CreateFamilyFiles = """
        CREATE TABLE IF NOT EXISTS family_files (
            id TEXT PRIMARY KEY,
            relative_path TEXT NOT NULL,
            file_name TEXT NOT NULL,
            revit_major_version INTEGER NOT NULL,
            imported_at_utc TEXT NOT NULL
        )
        """;

    public const string CreateFamilyAssets = """
        CREATE TABLE IF NOT EXISTS family_assets (
            id TEXT PRIMARY KEY,
            catalog_item_id TEXT NOT NULL,
            version_label TEXT,
            asset_type TEXT NOT NULL,
            file_name TEXT NOT NULL,
            relative_path TEXT NOT NULL,
            size_bytes INTEGER NOT NULL,
            description TEXT,
            created_at_utc TEXT NOT NULL,
            is_primary INTEGER DEFAULT 0,
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE
        )
        """;

    public const string CreateCatalogTags = """
        CREATE TABLE IF NOT EXISTS catalog_tags (
            catalog_item_id TEXT NOT NULL,
            tag TEXT NOT NULL,
            normalized_tag TEXT NOT NULL,
            PRIMARY KEY (catalog_item_id, tag),
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE
        )
        """;

    public const string CreateProjectUsage = """
        CREATE TABLE IF NOT EXISTS project_usage (
            id TEXT PRIMARY KEY,
            catalog_item_id TEXT NOT NULL,
            version_id TEXT,
            project_name TEXT,
            project_path TEXT,
            revit_major_version INTEGER,
            action TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE
        )
        """;

    public const string CreateCategories = """
        CREATE TABLE IF NOT EXISTS categories (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            parent_id TEXT,
            sort_order INTEGER NOT NULL DEFAULT 0,
            created_at_utc TEXT NOT NULL,
            FOREIGN KEY (parent_id) REFERENCES categories(id) ON DELETE CASCADE
        )
        """;

    public const string CreateCategoriesIndexes = """
        CREATE INDEX IF NOT EXISTS ix_categories_parent ON categories (parent_id)
        """;

    public const string MigrateV3AddCategoryIdColumn = """
        ALTER TABLE catalog_items ADD COLUMN category_id TEXT
        """;

    public const string CreateFamilyTypes = """
        CREATE TABLE IF NOT EXISTS family_types (
            id TEXT PRIMARY KEY,
            catalog_item_id TEXT NOT NULL,
            type_name TEXT NOT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0,
            version_id TEXT,
            file_id TEXT,
            extraction_run_id TEXT,
            type_unique_id TEXT,
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
            FOREIGN KEY (version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE,
            FOREIGN KEY (file_id) REFERENCES family_files(id) ON DELETE SET NULL,
            UNIQUE(catalog_item_id, version_id, type_name)
        )
        """;

    public const string CreateFamilyTypesIndexes = """
        CREATE INDEX IF NOT EXISTS ix_family_types_item ON family_types (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_family_types_version_id ON family_types (version_id) WHERE version_id IS NOT NULL;
        -- v2.1.0 (ADR-041 rev #2): orchestrator types have version_id IS NULL.
        -- SQLite treats NULL != NULL in composite UNIQUE, so the table-level
        -- UNIQUE(catalog_item_id, version_id, type_name) does not prevent
        -- duplicate orchestrator types. This partial unique index closes the
        -- gap: one (catalog_item_id, type_name) per item for NULL versions.
        CREATE UNIQUE INDEX IF NOT EXISTS ix_family_types_orchestrator_unique
        ON family_types (catalog_item_id, type_name)
        WHERE version_id IS NULL
        """;

    public const string CreateAttributePresets = """
        CREATE TABLE IF NOT EXISTS attribute_presets (
            id TEXT PRIMARY KEY,
            category_id TEXT,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            FOREIGN KEY (category_id) REFERENCES categories(id) ON DELETE CASCADE
        )
        """;

    public const string CreateAttributePresetParameters = """
        CREATE TABLE IF NOT EXISTS attribute_preset_parameters (
            preset_id TEXT NOT NULL,
            parameter_name TEXT NOT NULL,
            display_name TEXT,
            sort_order INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (preset_id, parameter_name),
            FOREIGN KEY (preset_id) REFERENCES attribute_presets(id) ON DELETE CASCADE
        )
        """;

    public const string CreateAttributePresetsIndexes = """
        CREATE UNIQUE INDEX IF NOT EXISTS idx_attribute_presets_category ON attribute_presets (category_id)
        """;

    public const string MigrateV5AddIsPrimaryColumn = """
        ALTER TABLE family_assets ADD COLUMN is_primary INTEGER DEFAULT 0
        """;

    public const string CreateAttributeDefinitions = """
        CREATE TABLE IF NOT EXISTS attribute_definitions (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL COLLATE NOCASE,
            group_name TEXT,
            is_active INTEGER NOT NULL DEFAULT 1,
            created_at_utc TEXT NOT NULL,
            UNIQUE(name)
        )
        """;

    public const string CreateCategoryAttributeBindings = """
        CREATE TABLE IF NOT EXISTS category_attribute_bindings (
            id TEXT PRIMARY KEY,
            category_id TEXT NOT NULL,
            attribute_id TEXT NOT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            FOREIGN KEY (category_id) REFERENCES categories(id) ON DELETE CASCADE,
            FOREIGN KEY (attribute_id) REFERENCES attribute_definitions(id) ON DELETE CASCADE,
            UNIQUE(category_id, attribute_id)
        )
        """;

    public const string CreateFamilyDataImportRuns = """
        CREATE TABLE IF NOT EXISTS family_data_import_runs (
            id TEXT PRIMARY KEY,
            catalog_item_id TEXT NOT NULL,
            version_id TEXT,
            file_id TEXT,
            revit_major_version INTEGER NOT NULL,
            status TEXT NOT NULL DEFAULT 'Succeeded',
            types_count INTEGER NOT NULL DEFAULT 0,
            started_at_utc TEXT NOT NULL,
            completed_at_utc TEXT,
            error_message TEXT,
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE
        )
        """;

    public const string CreateExtractedAttributeValues = """
        CREATE TABLE IF NOT EXISTS extracted_attribute_values (
            id TEXT PRIMARY KEY,
            catalog_item_id TEXT NOT NULL,
            version_id TEXT,
            file_id TEXT,
            type_id TEXT,
            attribute_id TEXT,
            binding_id TEXT,
            parameter_name TEXT NOT NULL,
            parameter_scope TEXT,
            storage_type TEXT,
            value_text TEXT,
            value_raw TEXT,
            value_number REAL,
            unit_type_id TEXT,
            status TEXT NOT NULL DEFAULT 'Found',
            message TEXT,
            extraction_run_id TEXT NOT NULL,
            extracted_at_utc TEXT NOT NULL,
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
            FOREIGN KEY (type_id) REFERENCES family_types(id) ON DELETE CASCADE,
            FOREIGN KEY (attribute_id) REFERENCES attribute_definitions(id) ON DELETE CASCADE,
            FOREIGN KEY (version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE,
            FOREIGN KEY (extraction_run_id) REFERENCES family_data_import_runs(id) ON DELETE CASCADE,
            UNIQUE(catalog_item_id, version_id, type_id, parameter_name)
        )
        """;

    public const string MigrateV6FamilyTypesAddColumns = """
        ALTER TABLE family_types ADD COLUMN version_id TEXT;
        ALTER TABLE family_types ADD COLUMN file_id TEXT;
        ALTER TABLE family_types ADD COLUMN extraction_run_id TEXT
        """;

    public const string CreateV6Indexes = """
        CREATE INDEX IF NOT EXISTS ix_attr_values_item_version ON extracted_attribute_values (catalog_item_id, version_id);
        CREATE INDEX IF NOT EXISTS ix_attr_values_type ON extracted_attribute_values (type_id);
        CREATE INDEX IF NOT EXISTS ix_attr_values_attribute ON extracted_attribute_values (attribute_id);
        CREATE INDEX IF NOT EXISTS ix_attr_values_attribute_text ON extracted_attribute_values (attribute_id, value_text);
        CREATE INDEX IF NOT EXISTS ix_attr_values_attribute_number ON extracted_attribute_values (attribute_id, value_number);
        CREATE INDEX IF NOT EXISTS ix_attr_bindings_category ON category_attribute_bindings (category_id);
        CREATE INDEX IF NOT EXISTS ix_attr_bindings_attribute ON category_attribute_bindings (attribute_id);
        CREATE UNIQUE INDEX IF NOT EXISTS ix_attr_definitions_name ON attribute_definitions (name COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS ix_import_runs_item ON family_data_import_runs (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_import_runs_started ON family_data_import_runs (started_at_utc)
        """;

    public const string CreateDbUsers = """
        CREATE TABLE IF NOT EXISTS db_users (
            user_id TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            role TEXT NOT NULL DEFAULT 'Engineer',
            status TEXT NOT NULL DEFAULT 'Active',
            joined_at_utc TEXT NOT NULL,
            last_seen_at_utc TEXT NOT NULL
        )
        """;

    public const string CreateDbUsersIndexes = """
        CREATE INDEX IF NOT EXISTS ix_db_users_role ON db_users (role);
        CREATE INDEX IF NOT EXISTS ix_db_users_status ON db_users (status)
        """;

    public const string MigrateV7AddOwnerIdentity = """
        ALTER TABLE database_meta ADD COLUMN owner_identity TEXT
        """;

    public const string MigrateV8RecreateExtractedAttributeValues = """
        -- Clean up any leftover from a previous failed migration
        DROP TABLE IF EXISTS extracted_attribute_values_new;
        -- Create temporary table with new schema. All four FK constraints
        -- are present so that if V8 runs AFTER V15 (via EnsureCriticalColumns
        -- path on a DB that never had V15 applied yet) the type_id and
        -- attribute_id FKs survive the recreate. Without them, Bug #85
        -- regression would lose cascade-cleanup for attribute values
        -- whenever the V8 path executes on an old DB.
        CREATE TABLE extracted_attribute_values_new (
            id TEXT PRIMARY KEY,
            catalog_item_id TEXT NOT NULL,
            version_id TEXT,
            file_id TEXT,
            type_id TEXT,
            attribute_id TEXT,
            binding_id TEXT,
            parameter_name TEXT NOT NULL,
            parameter_scope TEXT,
            storage_type TEXT,
            value_text TEXT,
            value_raw TEXT,
            value_number REAL,
            unit_type_id TEXT,
            status TEXT NOT NULL DEFAULT 'Found',
            message TEXT,
            extraction_run_id TEXT NOT NULL,
            extracted_at_utc TEXT NOT NULL,
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
            FOREIGN KEY (type_id) REFERENCES family_types(id) ON DELETE CASCADE,
            FOREIGN KEY (attribute_id) REFERENCES attribute_definitions(id) ON DELETE CASCADE,
            FOREIGN KEY (extraction_run_id) REFERENCES family_data_import_runs(id) ON DELETE CASCADE,
            UNIQUE(catalog_item_id, version_id, type_id, parameter_name)
        );
        -- Copy data from old table
        INSERT INTO extracted_attribute_values_new (
            id, catalog_item_id, version_id, file_id, type_id, attribute_id, binding_id,
            parameter_name, parameter_scope, storage_type, value_text, value_raw, value_number,
            unit_type_id, status, message, extraction_run_id, extracted_at_utc
        )
        SELECT
            id, catalog_item_id, version_id, file_id, type_id, attribute_id, binding_id,
            parameter_name, parameter_scope, storage_type, value_text, value_raw, value_number,
            unit_type_id, status, message, extraction_run_id, extracted_at_utc
        FROM extracted_attribute_values;
        -- Drop old table
        DROP TABLE extracted_attribute_values;
        -- Rename new table
        ALTER TABLE extracted_attribute_values_new RENAME TO extracted_attribute_values;
        -- Recreate indexes
        CREATE INDEX IF NOT EXISTS ix_attr_values_item_version ON extracted_attribute_values (catalog_item_id, version_id);
        CREATE INDEX IF NOT EXISTS ix_attr_values_type ON extracted_attribute_values (type_id);
        CREATE INDEX IF NOT EXISTS ix_attr_values_attribute ON extracted_attribute_values (attribute_id);
        CREATE INDEX IF NOT EXISTS ix_attr_values_attribute_text ON extracted_attribute_values (attribute_id, value_text);
        CREATE INDEX IF NOT EXISTS ix_attr_values_attribute_number ON extracted_attribute_values (attribute_id, value_number);
        """;

    public const string CreateTables = $"""
        {CreateDatabaseMeta};
        {CreateSchemaInfo};
        {CreateCatalogItems};
        {CreateCatalogVersions};
        {CreateFamilyFiles};
        {CreateFamilyAssets};
        {CreateCatalogTags};
        {CreateProjectUsage};
        {CreateCategories};
        {CreateFamilyTypes};
        {CreateAttributePresets};
        {CreateAttributePresetParameters};
        {CreateAttributeDefinitions};
        {CreateCategoryAttributeBindings};
        {CreateFamilyDataImportRuns};
        {CreateExtractedAttributeValues};
        {CreateDbUsers};
        {CreateFamilyNestedSharedFamilies}
        """;

    public const string MigrateV9AddLoadedVersionLabel = """
        ALTER TABLE project_usage ADD COLUMN loaded_version_label TEXT
        """;

    public const string MigrateV10AddFamilyTypesNameIndex = """
        CREATE INDEX IF NOT EXISTS ix_family_types_name ON family_types (type_name)
        """;

    public const string MigrateV11AddSystemFamilyColumns = """
        ALTER TABLE catalog_items ADD COLUMN family_source TEXT NOT NULL DEFAULT 'loadable';
        ALTER TABLE catalog_items ADD COLUMN revit_category TEXT;
        ALTER TABLE family_types ADD COLUMN type_unique_id TEXT
        """;

    public const string CreateV11Indexes = """
        CREATE INDEX IF NOT EXISTS ix_catalog_items_family_source ON catalog_items (family_source)
        """;

    public const string MigrateV12DropProjectUsageIndex = """
        DROP INDEX IF EXISTS ix_project_usage_lookup
        """;

    public const string MigrateV12DropProjectUsageTable = """
        DROP TABLE IF EXISTS project_usage
        """;

    public const string CreateFamilyNestedSharedFamilies = """
        CREATE TABLE IF NOT EXISTS family_nested_shared_families (
            catalog_item_id TEXT NOT NULL,
            version_id TEXT NOT NULL,
            -- nested_family_name is intentionally NOT COLLATE NOCASE.
            -- SQLite's NOCASE collation only handles ASCII case-folding
            -- (https://www.sqlite.org/datatype3.html#collation). Cyrillic
            -- (Russian) names like "Болт" vs "БОЛТ" are NOT considered equal
            -- by NOCASE. The C# layer (LocalSharedNestedFamilyRepository) does
            -- case-insensitive dedup with StringComparer.OrdinalIgnoreCase
            -- BEFORE INSERT, so duplicates are blocked at the application
            -- layer. The PK here acts as a safety net for ASCII-only fixtures
            -- and direct SQL inserts. If you bypass the C# layer for non-ASCII
            -- names, you will get duplicates — that is intentional.
            nested_family_name TEXT NOT NULL,
            ordinal INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (catalog_item_id, version_id, nested_family_name),
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
            FOREIGN KEY (version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE
        )
        """;

    public const string CreateNestedSharedFamiliesIndexes = """
        -- ix_nested_shared_name: intentionally removed. No current query filters
        -- by nested_family_name; the only SELECT uses catalog_item_id (covered
        -- by the PK prefix) and version_id. Add this index back when a
        -- "which families use this shared nested?" report is implemented.
        CREATE INDEX IF NOT EXISTS ix_nested_shared_version ON family_nested_shared_families (version_id)
        """;

    public const string CreateIndexes = """
        CREATE INDEX IF NOT EXISTS ix_catalog_items_normalized_name ON catalog_items (normalized_name);
        CREATE INDEX IF NOT EXISTS ix_catalog_items_category ON catalog_items (category_name);
        CREATE INDEX IF NOT EXISTS ix_catalog_items_status ON catalog_items (content_status);
        CREATE INDEX IF NOT EXISTS ix_catalog_versions_item ON catalog_versions (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_catalog_versions_item_label ON catalog_versions (catalog_item_id, version_label);
        CREATE INDEX IF NOT EXISTS ix_catalog_versions_file ON catalog_versions (file_id);
        CREATE INDEX IF NOT EXISTS ix_catalog_versions_revit ON catalog_versions (revit_major_version);
        CREATE INDEX IF NOT EXISTS ix_family_files_revit ON family_files (revit_major_version);
        CREATE INDEX IF NOT EXISTS ix_catalog_tags_item ON catalog_tags (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_catalog_tags_normalized ON catalog_tags (normalized_tag);
        CREATE INDEX IF NOT EXISTS ix_family_assets_item ON family_assets (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_family_assets_type ON family_assets (asset_type);
        CREATE INDEX IF NOT EXISTS ix_family_assets_item_version ON family_assets (catalog_item_id, version_label) WHERE version_label IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_categories_parent ON categories (parent_id);
        CREATE INDEX IF NOT EXISTS ix_family_types_item ON family_types (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_family_types_name ON family_types (type_name);
        CREATE INDEX IF NOT EXISTS ix_family_types_version_id ON family_types (version_id) WHERE version_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_attr_values_version ON extracted_attribute_values (version_id) WHERE version_id IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS idx_attribute_presets_category ON attribute_presets (category_id)
        """;

    /// <summary>
    /// v2.0.0 migration v14: drop sha256 / size_bytes columns. SQLite 3.35+
    /// supports ALTER TABLE DROP COLUMN, so we drop each column inside a
    /// single transaction. Indexes on the dropped columns are auto-removed.
    /// </summary>
    public const string MigrateV14DropSha256Columns = """
        ALTER TABLE family_files DROP COLUMN size_bytes;
        ALTER TABLE family_files DROP COLUMN sha256;
        ALTER TABLE catalog_versions DROP COLUMN sha256;
        ALTER TABLE family_data_import_runs DROP COLUMN source_sha256
        """;

    public const string MigrateV14DropSha256Indexes = """
        DROP INDEX IF EXISTS ix_family_files_sha256;
        DROP INDEX IF EXISTS ix_catalog_versions_sha256
        """;

    /// <summary>
    /// v2.0.0 (ADR-036) migration v15: add FOREIGN KEY (type_id) REFERENCES
    /// family_types(id) ON DELETE CASCADE on extracted_attribute_values. This
    /// is a prerequisite for SyncTypesAsync (the new unified type-save method)
    /// to clean up orphan attribute values via DB-level CASCADE when a type
    /// is removed from a family.
    ///
    /// SQLite does not support ALTER TABLE ADD CONSTRAINT, so we recreate
    /// the table inside a single transaction, copy data verbatim, and let the
    /// new FK constraints take effect on the renamed table. Pre-existing
    /// orphan rows (extracted_attribute_values.type_id pointing at a deleted
    /// family_types.id) are deleted before the recreate.
    ///
    /// Note: PRAGMA foreign_keys is a no-op inside a transaction (SQLite
    /// limitation), so we cannot toggle it for the recreate. The pre-delete
    /// of orphan rows is what makes the table-recreate safe.
    /// </summary>
    public const string MigrateV15AddAttributeValuesForeignKey = """
        -- 1. Clean up any orphan rows that would otherwise violate the new FK.
        --    extracted_attribute_values.type_id is nullable; only NOT-NULL
        --    values that don't match any family_types.id need cleanup.
        DELETE FROM extracted_attribute_values
        WHERE type_id IS NOT NULL
          AND type_id NOT IN (SELECT id FROM family_types);

        -- 2. Recreate extracted_attribute_values with the new FK constraint.
        DROP TABLE IF EXISTS extracted_attribute_values_new;

        CREATE TABLE extracted_attribute_values_new (
            id TEXT PRIMARY KEY,
            catalog_item_id TEXT NOT NULL,
            version_id TEXT,
            file_id TEXT,
            type_id TEXT,
            attribute_id TEXT,
            binding_id TEXT,
            parameter_name TEXT NOT NULL,
            parameter_scope TEXT,
            storage_type TEXT,
            value_text TEXT,
            value_raw TEXT,
            value_number REAL,
            unit_type_id TEXT,
            status TEXT NOT NULL DEFAULT 'Found',
            message TEXT,
            extraction_run_id TEXT NOT NULL,
            extracted_at_utc TEXT NOT NULL,
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
            FOREIGN KEY (type_id) REFERENCES family_types(id) ON DELETE CASCADE,
            FOREIGN KEY (attribute_id) REFERENCES attribute_definitions(id) ON DELETE CASCADE,
            FOREIGN KEY (extraction_run_id) REFERENCES family_data_import_runs(id) ON DELETE CASCADE,
            UNIQUE(catalog_item_id, version_id, type_id, parameter_name)
        );

        INSERT INTO extracted_attribute_values_new (
            id, catalog_item_id, version_id, file_id, type_id, attribute_id, binding_id,
            parameter_name, parameter_scope, storage_type, value_text, value_raw, value_number,
            unit_type_id, status, message, extraction_run_id, extracted_at_utc
        )
        SELECT
            id, catalog_item_id, version_id, file_id, type_id, attribute_id, binding_id,
            parameter_name, parameter_scope, storage_type, value_text, value_raw, value_number,
            unit_type_id, status, message, extraction_run_id, extracted_at_utc
        FROM extracted_attribute_values;

        DROP TABLE extracted_attribute_values;
        ALTER TABLE extracted_attribute_values_new RENAME TO extracted_attribute_values;

        -- 3. Recreate indexes (IF NOT EXISTS makes this idempotent).
        CREATE INDEX IF NOT EXISTS ix_attr_values_item_version ON extracted_attribute_values (catalog_item_id, version_id);
        CREATE INDEX IF NOT EXISTS ix_attr_values_type ON extracted_attribute_values (type_id);
        CREATE INDEX IF NOT EXISTS ix_attr_values_attribute ON extracted_attribute_values (attribute_id);
        CREATE INDEX IF NOT EXISTS ix_attr_values_attribute_text ON extracted_attribute_values (attribute_id, value_text);
        CREATE INDEX IF NOT EXISTS ix_attr_values_attribute_number ON extracted_attribute_values (attribute_id, value_number);
        """;

    /// <summary>
    /// v2.1.0 migration v16: add content_hash and hash_format_version
    /// columns to catalog_items and catalog_versions for content-fingerprint
    /// deduplication. Additive only — no breaking changes. Partial indexes
    /// (WHERE content_hash IS NOT NULL) avoid indexing legacy rows that have
    /// no hash yet.
    /// </summary>
    public const string MigrateV16AddContentHashColumns = """
        ALTER TABLE catalog_items ADD COLUMN content_hash TEXT;
        ALTER TABLE catalog_items ADD COLUMN hash_format_version INTEGER;
        ALTER TABLE catalog_versions ADD COLUMN content_hash TEXT;
        ALTER TABLE catalog_versions ADD COLUMN hash_format_version INTEGER
        """;

    public const string CreateV16Indexes = """
        CREATE INDEX IF NOT EXISTS ix_catalog_items_content_hash
            ON catalog_items (content_hash) WHERE content_hash IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_catalog_versions_content_hash
            ON catalog_versions (content_hash) WHERE content_hash IS NOT NULL
        """;

    /// <summary>
    /// v2.0.0 (ADR-041) migration v17: add FOREIGN KEY (version_id) REFERENCES
    /// catalog_versions(id) ON DELETE CASCADE on family_types. SQLite does not
    /// support ALTER TABLE ADD CONSTRAINT, so we recreate the table inside a
    /// single transaction, copy data verbatim (after orphan cleanup), and let
    /// the new FK constraints take effect on the renamed table. Pre-existing
    /// orphan rows (family_types.version_id pointing at a deleted
    /// catalog_versions.id) are deleted before the recreate — same pattern as
    /// V15 (extracted_attribute_values.type_id FK).
    ///
    /// This FK is what allows DeleteVersionAsync to remove types via a single
    /// `DELETE FROM catalog_versions WHERE version_label=@label` instead of
    /// manually walking family_types + extracted_attribute_values.
    ///
    /// NOTE: V17 keeps the legacy UNIQUE(catalog_item_id, type_name). V18
    /// migrates it to UNIQUE(catalog_item_id, version_id, type_name) for
    /// per-version type storage.
    /// </summary>
    public const string MigrateV17RecreateFamilyTypesWithVersionFk = """
        -- 1. Clean up orphan rows: family_types.version_id pointing at non-existent catalog_versions.id
        DELETE FROM family_types
        WHERE version_id IS NOT NULL
          AND version_id NOT IN (SELECT id FROM catalog_versions);

        -- 2. Recreate family_types with the new FK constraint on version_id
        DROP TABLE IF EXISTS family_types_v17;

        CREATE TABLE family_types_v17 (
            id TEXT PRIMARY KEY,
            catalog_item_id TEXT NOT NULL,
            type_name TEXT NOT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0,
            version_id TEXT,
            file_id TEXT,
            extraction_run_id TEXT,
            type_unique_id TEXT,
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
            FOREIGN KEY (version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE,
            FOREIGN KEY (file_id) REFERENCES family_files(id) ON DELETE SET NULL,
            UNIQUE(catalog_item_id, type_name)
        );

        INSERT INTO family_types_v17 (id, catalog_item_id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id)
        SELECT id, catalog_item_id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id
        FROM family_types;

        DROP TABLE family_types;
        ALTER TABLE family_types_v17 RENAME TO family_types;

        -- 3. Recreate indexes (IF NOT EXISTS makes this idempotent)
        CREATE INDEX IF NOT EXISTS ix_family_types_item ON family_types (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_family_types_name ON family_types (type_name);
        CREATE INDEX IF NOT EXISTS ix_family_types_version_id ON family_types (version_id) WHERE version_id IS NOT NULL
        """;

    /// <summary>
    /// v2.0.0 (ADR-041) migration v17 step 2: add FOREIGN KEY (version_id)
    /// REFERENCES catalog_versions(id) ON DELETE CASCADE on
    /// extracted_attribute_values. Same recreate-and-copy pattern as V15.
    /// orphan rows (extracted_attribute_values.version_id pointing at a deleted
    /// catalog_versions.id) are deleted before the recreate.
    /// </summary>
    public const string MigrateV17RecreateExtractedAttributeValuesWithVersionFk = """
        -- 1. Clean up any orphan rows that would otherwise violate the new FK.
        DELETE FROM extracted_attribute_values
        WHERE version_id IS NOT NULL
          AND version_id NOT IN (SELECT id FROM catalog_versions);

        -- 2. Recreate extracted_attribute_values with the new FK constraint on version_id.
        DROP TABLE IF EXISTS extracted_attribute_values_v17;

        CREATE TABLE extracted_attribute_values_v17 (
            id TEXT PRIMARY KEY,
            catalog_item_id TEXT NOT NULL,
            version_id TEXT,
            file_id TEXT,
            type_id TEXT,
            attribute_id TEXT,
            binding_id TEXT,
            parameter_name TEXT NOT NULL,
            parameter_scope TEXT,
            storage_type TEXT,
            value_text TEXT,
            value_raw TEXT,
            value_number REAL,
            unit_type_id TEXT,
            status TEXT NOT NULL DEFAULT 'Found',
            message TEXT,
            extraction_run_id TEXT NOT NULL,
            extracted_at_utc TEXT NOT NULL,
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
            FOREIGN KEY (type_id) REFERENCES family_types(id) ON DELETE CASCADE,
            FOREIGN KEY (attribute_id) REFERENCES attribute_definitions(id) ON DELETE CASCADE,
            FOREIGN KEY (version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE,
            FOREIGN KEY (extraction_run_id) REFERENCES family_data_import_runs(id) ON DELETE CASCADE,
            UNIQUE(catalog_item_id, version_id, type_id, parameter_name)
        );

        INSERT INTO extracted_attribute_values_v17 (
            id, catalog_item_id, version_id, file_id, type_id, attribute_id, binding_id,
            parameter_name, parameter_scope, storage_type, value_text, value_raw, value_number,
            unit_type_id, status, message, extraction_run_id, extracted_at_utc
        )
        SELECT
            id, catalog_item_id, version_id, file_id, type_id, attribute_id, binding_id,
            parameter_name, parameter_scope, storage_type, value_text, value_raw, value_number,
            unit_type_id, status, message, extraction_run_id, extracted_at_utc
        FROM extracted_attribute_values;

        DROP TABLE extracted_attribute_values;
        ALTER TABLE extracted_attribute_values_v17 RENAME TO extracted_attribute_values;

        -- 3. Recreate indexes (IF NOT EXISTS makes this idempotent)
        CREATE INDEX IF NOT EXISTS ix_attr_values_item_version ON extracted_attribute_values (catalog_item_id, version_id);
        CREATE INDEX IF NOT EXISTS ix_attr_values_type ON extracted_attribute_values (type_id);
        CREATE INDEX IF NOT EXISTS ix_attr_values_attribute ON extracted_attribute_values (attribute_id);
        CREATE INDEX IF NOT EXISTS ix_attr_values_attribute_text ON extracted_attribute_values (attribute_id, value_text);
        CREATE INDEX IF NOT EXISTS ix_attr_values_attribute_number ON extracted_attribute_values (attribute_id, value_number);
        CREATE INDEX IF NOT EXISTS ix_attr_values_version ON extracted_attribute_values (version_id) WHERE version_id IS NOT NULL
        """;

    /// <summary>
    /// v2.0.0 (ADR-041) migration v17 step 3: composite index on
    /// (catalog_item_id, version_label) for fast version-by-label lookups
    /// used by GetVersionByLabelAsync and SetActiveVersionAsync.
    /// </summary>
    public const string CreateV17Indexes = """
        CREATE INDEX IF NOT EXISTS ix_catalog_versions_item_label
        ON catalog_versions (catalog_item_id, version_label)
        """;

    /// <summary>
    /// v2.1.0 (ADR-041 rev #2) migration v18: change
    /// UNIQUE(catalog_item_id, type_name) →
    /// UNIQUE(catalog_item_id, version_id, type_name) so the same type name
    /// ("100", "200") can co-exist in multiple versions of the same catalog
    /// item. Without this, SyncTypesAsync's version-scoped DELETE + INSERT
    /// for a new version with the same type names as the previous version
    /// would UPSERT (via ON CONFLICT) and silently reassign
    /// family_types.version_id to the new version — destroying the previous
    /// version's type rows.
    ///
    /// Same recreate-and-copy pattern as V15/V17 because SQLite does not
    /// support altering a UNIQUE constraint in place. Orphan rows
    /// (family_types.version_id not in catalog_versions) are cleaned up
    /// before the copy — same defensive pattern as V17.
    ///
    /// A partial UNIQUE INDEX on (catalog_item_id, type_name)
    /// WHERE version_id IS NULL protects the orchestrator case
    /// (LoadableFamilyImportOrchestrator / SystemFamilyImportOrchestrator
    /// import families from the project without a version handle). SQLite
    /// treats NULL != NULL in composite UNIQUE, so without this partial
    /// index, two rows with the same (catalog_item_id, null version_id,
    /// type_name) would violate the "one type per name" invariant for
    /// project families.
    /// </summary>
    public const string MigrateV18RecreateFamilyTypesPerVersionUnique = """
        -- 1. Clean up orphan rows (defensive — same as V17).
        DELETE FROM family_types
        WHERE version_id IS NOT NULL
          AND version_id NOT IN (SELECT id FROM catalog_versions);

        -- 2. Recreate family_types with per-version UNIQUE.
        DROP TABLE IF EXISTS family_types_v18;

        CREATE TABLE family_types_v18 (
            id TEXT PRIMARY KEY,
            catalog_item_id TEXT NOT NULL,
            type_name TEXT NOT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0,
            version_id TEXT,
            file_id TEXT,
            extraction_run_id TEXT,
            type_unique_id TEXT,
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
            FOREIGN KEY (version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE,
            FOREIGN KEY (file_id) REFERENCES family_files(id) ON DELETE SET NULL,
            UNIQUE(catalog_item_id, version_id, type_name)
        );

        INSERT INTO family_types_v18 (id, catalog_item_id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id)
        SELECT id, catalog_item_id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id
        FROM family_types;

        DROP TABLE family_types;
        ALTER TABLE family_types_v18 RENAME TO family_types;

        -- 3. Recreate indexes and the orchestrator partial UNIQUE index.
        CREATE INDEX IF NOT EXISTS ix_family_types_item ON family_types (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_family_types_name ON family_types (type_name);
        CREATE INDEX IF NOT EXISTS ix_family_types_version_id ON family_types (version_id) WHERE version_id IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ix_family_types_orchestrator_unique
        ON family_types (catalog_item_id, type_name)
        WHERE version_id IS NULL
        """;

    /// <summary>
    /// V19: adds <c>published_by</c> column to <c>catalog_versions</c>
    /// so each version row records which Revit user published it.
    /// ADR-041 rev #5. Simple ALTER TABLE ADD COLUMN (nullable, no default)
    /// — no recreate needed. Existing rows get NULL (unknown author).
    /// </summary>
    public const string MigrateV19AddPublishedByColumn = """
        ALTER TABLE catalog_versions ADD COLUMN published_by TEXT
        """;

    /// <summary>
    /// V20 (ADR-045 / #119): adds <c>base_type INTEGER NOT NULL DEFAULT 0</c>
    /// column to <c>database_meta</c>. The base-type itself lives in
    /// <c>registry.json</c> as the single source of truth (decision A1); this
    /// column is a convenience cache for RBAC / migration tooling. <c>0</c>
    /// = <see cref="SmartCon.Core.Models.FamilyManager.BaseType.General"/>,
    /// <c>1</c> = <see cref="SmartCon.Core.Models.FamilyManager.BaseType.Project"/>.
    /// </summary>
    public const string MigrateV20AddBaseTypeColumn = """
        ALTER TABLE database_meta ADD COLUMN base_type INTEGER NOT NULL DEFAULT 0
        """;

    /// V21 (#119 reconnect): adds project_binding_json TEXT to database_meta so that
    /// project base configuration survives DisconnectDatabaseAsync + ConnectDatabaseAsync.
    public const string MigrateV21AddProjectBindingColumn = """
        ALTER TABLE database_meta ADD COLUMN project_binding_json TEXT
        """;
}

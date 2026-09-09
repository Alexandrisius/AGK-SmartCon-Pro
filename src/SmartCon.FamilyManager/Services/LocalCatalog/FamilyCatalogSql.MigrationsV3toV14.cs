namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal static partial class FamilyCatalogSql
{
    public const string MigrateV3AddCategoryIdColumn = """
        ALTER TABLE catalog_items ADD COLUMN category_id TEXT
        """;

    public const string MigrateV5AddIsPrimaryColumn = """
        ALTER TABLE family_assets ADD COLUMN is_primary INTEGER DEFAULT 0
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
}

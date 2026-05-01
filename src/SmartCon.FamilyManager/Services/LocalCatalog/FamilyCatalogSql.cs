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
            sha256 TEXT NOT NULL,
            revit_major_version INTEGER NOT NULL,
            types_count INTEGER,
            parameters_count INTEGER,
            published_at_utc TEXT NOT NULL,
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
            size_bytes INTEGER NOT NULL,
            sha256 TEXT NOT NULL,
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
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
            UNIQUE(catalog_item_id, type_name)
        )
        """;

    public const string CreateFamilyTypesIndexes = """
        CREATE INDEX IF NOT EXISTS ix_family_types_item ON family_types (catalog_item_id)
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
        {CreateAttributePresetParameters}
        """;

    public const string CreateIndexes = """
        CREATE INDEX IF NOT EXISTS ix_catalog_items_normalized_name ON catalog_items (normalized_name);
        CREATE INDEX IF NOT EXISTS ix_catalog_items_category ON catalog_items (category_name);
        CREATE INDEX IF NOT EXISTS ix_catalog_items_status ON catalog_items (content_status);
        CREATE INDEX IF NOT EXISTS ix_catalog_versions_item ON catalog_versions (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_catalog_versions_file ON catalog_versions (file_id);
        CREATE INDEX IF NOT EXISTS ix_catalog_versions_sha256 ON catalog_versions (sha256);
        CREATE INDEX IF NOT EXISTS ix_catalog_versions_revit ON catalog_versions (revit_major_version);
        CREATE INDEX IF NOT EXISTS ix_family_files_sha256 ON family_files (sha256);
        CREATE INDEX IF NOT EXISTS ix_family_files_revit ON family_files (revit_major_version);
        CREATE INDEX IF NOT EXISTS ix_catalog_tags_item ON catalog_tags (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_catalog_tags_normalized ON catalog_tags (normalized_tag);
        CREATE INDEX IF NOT EXISTS ix_project_usage_item ON project_usage (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_project_usage_path ON project_usage (project_path);
        CREATE INDEX IF NOT EXISTS ix_project_usage_created ON project_usage (created_at_utc);
        CREATE INDEX IF NOT EXISTS ix_family_assets_item ON family_assets (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_family_assets_type ON family_assets (asset_type);
        CREATE INDEX IF NOT EXISTS ix_categories_parent ON categories (parent_id);
        CREATE INDEX IF NOT EXISTS ix_family_types_item ON family_types (catalog_item_id);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_attribute_presets_category ON attribute_presets (category_id)
        """;
}

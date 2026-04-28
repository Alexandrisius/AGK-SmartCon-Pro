namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal static class FamilyCatalogSql
{
    public const string CreateSchemaInfo = """
        CREATE TABLE IF NOT EXISTS schema_info (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        )
        """;

    public const string CreateCatalogItems = """
        CREATE TABLE IF NOT EXISTS catalog_items (
            id TEXT PRIMARY KEY,
            provider_id TEXT NOT NULL DEFAULT 'local',
            name TEXT NOT NULL,
            normalized_name TEXT NOT NULL,
            description TEXT NULL,
            category_name TEXT NULL,
            manufacturer TEXT NULL,
            status TEXT NOT NULL DEFAULT 'Draft',
            current_version_id TEXT NULL,
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
            revit_major_version INTEGER NULL,
            types_count INTEGER NULL,
            parameters_count INTEGER NULL,
            imported_at_utc TEXT NOT NULL
        )
        """;

    public const string CreateFamilyFiles = """
        CREATE TABLE IF NOT EXISTS family_files (
            id TEXT PRIMARY KEY,
            original_path TEXT NULL,
            cached_path TEXT NULL,
            file_name TEXT NOT NULL,
            size_bytes INTEGER NOT NULL,
            sha256 TEXT NOT NULL,
            last_write_time_utc TEXT NULL,
            storage_mode TEXT NOT NULL DEFAULT 'Cached'
        )
        """;

    public const string CreateFamilyTypes = """
        CREATE TABLE IF NOT EXISTS family_types (
            id TEXT PRIMARY KEY,
            version_id TEXT NOT NULL,
            name TEXT NOT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0
        )
        """;

    public const string CreateFamilyParameters = """
        CREATE TABLE IF NOT EXISTS family_parameters (
            id TEXT PRIMARY KEY,
            version_id TEXT NOT NULL,
            type_id TEXT NULL,
            name TEXT NOT NULL,
            storage_type TEXT NULL,
            value_text TEXT NULL,
            is_instance INTEGER NULL,
            is_readonly INTEGER NULL,
            forge_type_id TEXT NULL
        )
        """;

    public const string CreateCatalogTags = """
        CREATE TABLE IF NOT EXISTS catalog_tags (
            catalog_item_id TEXT NOT NULL,
            tag TEXT NOT NULL,
            normalized_tag TEXT NOT NULL,
            PRIMARY KEY (catalog_item_id, tag)
        )
        """;

    public const string CreateProjectUsage = """
        CREATE TABLE IF NOT EXISTS project_usage (
            id TEXT PRIMARY KEY,
            catalog_item_id TEXT NOT NULL,
            version_id TEXT NOT NULL,
            provider_id TEXT NOT NULL,
            project_fingerprint TEXT NOT NULL,
            action TEXT NOT NULL,
            created_at_utc TEXT NOT NULL
        )
        """;

    public const string CreatePreviews = """
        CREATE TABLE IF NOT EXISTS previews (
            id TEXT PRIMARY KEY,
            catalog_item_id TEXT NOT NULL,
            version_id TEXT NULL,
            relative_path TEXT NOT NULL,
            width INTEGER NULL,
            height INTEGER NULL,
            created_at_utc TEXT NOT NULL
        )
        """;

    public const string CreateTables = $"""
        {CreateSchemaInfo};
        {CreateCatalogItems};
        {CreateCatalogVersions};
        {CreateFamilyFiles};
        {CreateFamilyTypes};
        {CreateFamilyParameters};
        {CreateCatalogTags};
        {CreateProjectUsage};
        {CreatePreviews}
        """;

    public const string CreateIndexes = """
        CREATE INDEX IF NOT EXISTS ix_catalog_items_normalized_name ON catalog_items (normalized_name);
        CREATE INDEX IF NOT EXISTS ix_catalog_items_category ON catalog_items (category_name);
        CREATE INDEX IF NOT EXISTS ix_catalog_items_status ON catalog_items (status);
        CREATE INDEX IF NOT EXISTS ix_catalog_items_provider ON catalog_items (provider_id);
        CREATE INDEX IF NOT EXISTS ix_catalog_versions_item ON catalog_versions (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_catalog_versions_file ON catalog_versions (file_id);
        CREATE INDEX IF NOT EXISTS ix_catalog_versions_sha256 ON catalog_versions (sha256);
        CREATE INDEX IF NOT EXISTS ix_family_files_sha256 ON family_files (sha256);
        CREATE INDEX IF NOT EXISTS ix_family_files_storage ON family_files (storage_mode);
        CREATE INDEX IF NOT EXISTS ix_family_types_version ON family_types (version_id);
        CREATE INDEX IF NOT EXISTS ix_family_parameters_version ON family_parameters (version_id);
        CREATE INDEX IF NOT EXISTS ix_family_parameters_type ON family_parameters (type_id);
        CREATE INDEX IF NOT EXISTS ix_catalog_tags_item ON catalog_tags (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_catalog_tags_normalized ON catalog_tags (normalized_tag);
        CREATE INDEX IF NOT EXISTS ix_project_usage_item ON project_usage (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_project_usage_project ON project_usage (project_fingerprint);
        CREATE INDEX IF NOT EXISTS ix_project_usage_created ON project_usage (created_at_utc);
        CREATE INDEX IF NOT EXISTS ix_previews_item ON previews (catalog_item_id)
        """;
}

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal static partial class FamilyCatalogSql
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
            revit_category_id INTEGER,
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
            glb_state INTEGER,
            es_marker_version INTEGER NOT NULL DEFAULT 0,
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
            family_name TEXT NOT NULL DEFAULT '',
            family_key TEXT NOT NULL DEFAULT '',
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
            FOREIGN KEY (version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE,
            FOREIGN KEY (file_id) REFERENCES family_files(id) ON DELETE SET NULL,
            UNIQUE(catalog_item_id, version_id, family_name, type_name)
        )
        """;

    public const string CreateFamilyTypesIndexes = """
        CREATE INDEX IF NOT EXISTS ix_family_types_item ON family_types (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_family_types_version_id ON family_types (version_id) WHERE version_id IS NOT NULL;
        -- v2.1.0 (ADR-041 rev #2): orchestrator types have version_id IS NULL.
        -- SQLite treats NULL != NULL in composite UNIQUE, so the table-level
        -- UNIQUE(catalog_item_id, version_id, family_name, type_name) does
        -- not prevent duplicate orchestrator types. This partial unique
        -- index closes the gap: one (catalog_item_id, family_name,
        -- type_name) per item for NULL versions. V26 (#183): family_name
        -- added — a system type is identified by (family, name), never by
        -- name alone.
        CREATE UNIQUE INDEX IF NOT EXISTS ix_family_types_orchestrator_unique
        ON family_types (catalog_item_id, family_name, type_name)
        WHERE version_id IS NULL
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
        {CreateCategoryValidationRules};
        {CreateCategoryAssignmentRuleGroups};
        {CreateCategoryAssignmentConditions};
        {CreateFamilyDataImportRuns};
        {CreateExtractedAttributeValues};
        {CreateDbUsers};
        {CreateFamilyNestedSharedFamilies};
        {CreateFamilyDependencies};
        {CreateFamilyFacts};
        {CreateFamilyTypeHashes};
        {CreateFamilyRoutingRules};
        {CreateFamilyRoutingTypeSettings}
        """;

    /// <summary>
    /// V22 (ADR-055): category-driven facts extracted from the family file
    /// (e.g. Part Type for fitting categories). One row per
    /// (catalog_item, fact). <c>value_key</c> is the stable machine value
    /// (enum ordinal string; empty string = evaluated-but-absent sentinel),
    /// <c>value_display</c> the extraction-time human fallback. Facts are
    /// item-level metadata and never participate in dedup.
    /// </summary>
    public const string CreateFamilyFacts = """
        CREATE TABLE IF NOT EXISTS family_facts (
            catalog_item_id TEXT NOT NULL,
            fact_key TEXT NOT NULL,
            value_key TEXT NOT NULL,
            value_display TEXT NOT NULL,
            PRIMARY KEY (catalog_item_id, fact_key),
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE
        )
        """;

    public const string CreateFamilyFactsIndexes = """
        CREATE INDEX IF NOT EXISTS ix_family_facts_key ON family_facts (fact_key, value_key)
        """;

    /// <summary>
    /// V32 (Issue #249, Phase 2): per-type content hashes of a catalog
    /// version — one row per (version, type). <c>type_identity_key</c> is
    /// computed in C# (<c>typeName.ToUpperInvariant()</c> for loadable,
    /// <c>SystemTypeIdentityKey.Build</c> "TOKEN|NAME" for system — one
    /// category can hold same-named types of different system families,
    /// FHV6), so the key is culture-correct for Cyrillic unlike SQLite's
    /// NOCASE collation (see the comment in
    /// <see cref="CreateFamilyNestedSharedFamilies"/>). Populated at import
    /// from the Prepare-time snapshot hashes and backfilled for legacy
    /// versions by the optional <c>type-hashes-v1</c> actualization task.
    /// Typeless loadable families legitimately have ZERO rows — the
    /// backfill detection keys off <c>family_types</c>, not off this table.
    /// </summary>
    public const string CreateFamilyTypeHashes = """
        CREATE TABLE IF NOT EXISTS family_type_hashes (
            catalog_version_id TEXT NOT NULL,
            type_identity_key TEXT NOT NULL,
            type_name TEXT NOT NULL,
            type_hash TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            PRIMARY KEY (catalog_version_id, type_identity_key),
            FOREIGN KEY (catalog_version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE
        )
        """;

    public const string CreateFamilyTypeHashesIndexes = """
        CREATE INDEX IF NOT EXISTS ix_family_type_hashes_hash ON family_type_hashes (type_hash);
        CREATE INDEX IF NOT EXISTS ix_family_type_hashes_key ON family_type_hashes (type_identity_key)
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

    /// <summary>
    /// V29 (#207, ADR-066): parent→child dependency links between catalog
    /// items. <c>dependency_kind</c> discriminates the dependency class
    /// (<c>routing</c> — fitting families referenced by a system MEPCurve
    /// type's RoutingPreferenceManager rules; <c>shared_nested</c> — shared
    /// nested families of a loadable parent). <c>part_name</c> keeps the
    /// original "Family:Type" token of the routing rule so a rule can be
    /// matched to a link without re-parsing the parent snapshot. The link is
    /// version-scoped for history/audit (the parent's staging version that
    /// declared the dependency); sync reads links of the parent's CURRENT
    /// version (join by <c>current_version_label</c>) and always resolves
    /// the child's ACTIVE version — the stored version ids never drive
    /// version selection. V30 (E2, #209): <c>child_version_label</c> records
    /// which child version was EMBEDDED in the parent's version at import
    /// time — compared against the child's <c>current_version_label</c> for
    /// dependency-drift detection (pure SQL); NULL = unknown (legacy V29
    /// links) = never drifted.
    /// </summary>
    public const string CreateFamilyDependencies = """
        CREATE TABLE IF NOT EXISTS family_dependencies (
            parent_catalog_item_id TEXT NOT NULL,
            parent_version_id TEXT NOT NULL,
            child_catalog_item_id TEXT NOT NULL,
            dependency_kind TEXT NOT NULL,
            part_name TEXT,
            ordinal INTEGER NOT NULL DEFAULT 0,
            child_version_label TEXT,
            PRIMARY KEY (parent_catalog_item_id, parent_version_id, child_catalog_item_id, dependency_kind),
            FOREIGN KEY (parent_catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
            FOREIGN KEY (parent_version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE,
            FOREIGN KEY (child_catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE
        )
        """;

    public const string CreateFamilyDependenciesIndexes = """
        CREATE INDEX IF NOT EXISTS ix_family_dependencies_parent ON family_dependencies (parent_catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_family_dependencies_child ON family_dependencies (child_catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_family_dependencies_version ON family_dependencies (parent_version_id)
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
        CREATE UNIQUE INDEX IF NOT EXISTS idx_attribute_presets_category ON attribute_presets (category_id);
        """ + CreateFamilyDependenciesIndexes + ";" + CreateCategoryAssignmentRuleIndexes + ";" + CreateFamilyTypeHashesIndexes + ";" + CreateFamilyRoutingRulesIndexes;

}

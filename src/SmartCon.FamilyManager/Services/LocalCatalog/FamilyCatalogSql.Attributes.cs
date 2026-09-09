namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal static partial class FamilyCatalogSql
{
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

    public const string CreateCategoryValidationRules = """
        CREATE TABLE IF NOT EXISTS category_validation_rules (
            id TEXT PRIMARY KEY,
            binding_id TEXT NOT NULL,
            operator TEXT NOT NULL,
            value_text TEXT,
            value_number REAL,
            min_value REAL,
            max_value REAL,
            unit_type_id TEXT,
            sort_order INTEGER NOT NULL DEFAULT 0,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            FOREIGN KEY (binding_id) REFERENCES category_attribute_bindings(id) ON DELETE CASCADE
        )
        """;

    public const string CreateCategoryValidationRulesIndexes = """
        CREATE INDEX IF NOT EXISTS ix_validation_rules_binding ON category_validation_rules (binding_id)
        """;

    /// <summary>
    /// V31 (#241): auto-assignment OR-groups per catalog category. The
    /// family matches the category when at least one enabled group of that
    /// category has all its enabled conditions satisfied.
    /// </summary>
    public const string CreateCategoryAssignmentRuleGroups = """
        CREATE TABLE IF NOT EXISTS category_assignment_rule_groups (
            id TEXT PRIMARY KEY,
            category_id TEXT NOT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            FOREIGN KEY (category_id) REFERENCES categories(id) ON DELETE CASCADE
        )
        """;

    /// <summary>
    /// V31 (#241): one AND-condition inside an assignment group. Exactly
    /// one of attribute_id / system_key is set, per source_kind (CHECK
    /// constraint — hand-edited databases cannot poison the engine).
    /// Ordinal values (Revit category, Part Type) live in value_text as
    /// invariant ordinal strings.
    /// </summary>
    public const string CreateCategoryAssignmentConditions = """
        CREATE TABLE IF NOT EXISTS category_assignment_conditions (
            id TEXT PRIMARY KEY,
            group_id TEXT NOT NULL,
            source_kind TEXT NOT NULL,
            attribute_id TEXT,
            system_key TEXT,
            operator TEXT NOT NULL,
            value_text TEXT,
            value_number REAL,
            min_value REAL,
            max_value REAL,
            sort_order INTEGER NOT NULL DEFAULT 0,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            FOREIGN KEY (group_id) REFERENCES category_assignment_rule_groups(id) ON DELETE CASCADE,
            FOREIGN KEY (attribute_id) REFERENCES attribute_definitions(id) ON DELETE CASCADE,
            CHECK (
                (source_kind = 'attribute' AND attribute_id IS NOT NULL AND system_key IS NULL)
                OR (source_kind = 'system' AND system_key IS NOT NULL AND attribute_id IS NULL)
            )
        )
        """;

    public const string CreateCategoryAssignmentRuleIndexes = """
        CREATE INDEX IF NOT EXISTS ix_assignment_groups_category ON category_assignment_rule_groups (category_id);
        CREATE INDEX IF NOT EXISTS ix_assignment_conditions_group ON category_assignment_conditions (group_id)
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
}

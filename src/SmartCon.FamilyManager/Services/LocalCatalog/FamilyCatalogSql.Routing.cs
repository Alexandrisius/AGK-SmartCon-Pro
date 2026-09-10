namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal static partial class FamilyCatalogSql
{
    /// <summary>
    /// V35 (#254, ADR-072 Phase 2b): per-version tracking column of the
    /// routing backfill/slimming actualization — 0 = pending, 1 = done,
    /// -1 = unreadable (terminal), -2 = missing file (terminal). Detection
    /// keys off THIS column, never off the absence of rows in
    /// <c>family_routing_rules</c> (a legitimately routing-less type has
    /// none — validator amendment).
    /// </summary>
    public const string MigrateV35AddRoutingBackfilled = """
        ALTER TABLE catalog_versions ADD COLUMN routing_backfilled INTEGER NOT NULL DEFAULT 0
        """;

    /// <summary>
    /// V36 (ADR-072, Phase 3): per-version segment size tables — the
    /// routing editor's min/max dropdown source (segment nominal
    /// diameters, mirroring the Revit routing dialog). Diameters in
    /// internal units (feet); cascade-deleted with the version.
    /// </summary>
    public const string MigrateV36AddSegmentSizes = """
        CREATE TABLE IF NOT EXISTS family_segment_sizes (
            catalog_version_id TEXT NOT NULL,
            segment_name TEXT NOT NULL,
            nominal_diameter REAL NOT NULL,
            inner_diameter REAL NOT NULL,
            outer_diameter REAL NOT NULL,
            used_in_size_lists INTEGER NOT NULL DEFAULT 0,
            used_in_sizing INTEGER NOT NULL DEFAULT 0,
            sort_order INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (catalog_version_id, segment_name, nominal_diameter),
            FOREIGN KEY (catalog_version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_family_segment_sizes_version ON family_segment_sizes (catalog_version_id);
        """;

    /// <summary>
    /// V37 (ADR-072 World B, owner decision 2026-08-29): ITEM-level routing
    /// link tables — routing left the content hash and the version model
    /// (it is a link between catalog families, not file content). The
    /// migration copies the current version's V34 rows so curated links
    /// survive the upgrade; items whose current version carries no V34 rows
    /// stay empty and get seeded by import/backfill on first sight.
    /// </summary>
    public const string MigrateV37AddItemRoutingTables = """
        CREATE TABLE IF NOT EXISTS item_routing_rules (
            catalog_item_id TEXT NOT NULL,
            family_key TEXT NOT NULL DEFAULT '',
            type_name TEXT NOT NULL,
            group_key TEXT NOT NULL,
            rule_order INTEGER NOT NULL,
            part_name TEXT,
            description TEXT NOT NULL DEFAULT '',
            criteria_json TEXT NOT NULL DEFAULT '[]',
            PRIMARY KEY (catalog_item_id, family_key, type_name, group_key, rule_order),
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS item_routing_type_settings (
            catalog_item_id TEXT NOT NULL,
            family_key TEXT NOT NULL DEFAULT '',
            type_name TEXT NOT NULL,
            preferred_junction_type INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (catalog_item_id, family_key, type_name),
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE
        );
        INSERT OR IGNORE INTO item_routing_rules
            (catalog_item_id, family_key, type_name, group_key, rule_order,
             part_name, description, criteria_json)
        SELECT r.catalog_item_id, r.family_key, r.type_name, r.group_key, r.rule_order,
             r.part_name, r.description, r.criteria_json
        FROM family_routing_rules r
        INNER JOIN catalog_versions cv ON cv.id = r.catalog_version_id
        INNER JOIN catalog_items ci
            ON ci.id = r.catalog_item_id AND ci.current_version_label = cv.version_label;
        INSERT OR IGNORE INTO item_routing_type_settings
            (catalog_item_id, family_key, type_name, preferred_junction_type)
        SELECT cv.catalog_item_id, s.family_key, s.type_name, s.preferred_junction_type
        FROM family_routing_type_settings s
        INNER JOIN catalog_versions cv ON cv.id = s.catalog_version_id
        INNER JOIN catalog_items ci
            ON ci.id = cv.catalog_item_id AND ci.current_version_label = cv.version_label;
        """;

    /// <summary>
    /// V38 (FHV21, owner decision 2026-09-01, stress test баг 3): PER-VERSION
    /// segment routing rules — the mini-project owns the whole segment
    /// configuration (set + order + size-range criterion), so it is
    /// versioned content like <c>family_segment_sizes</c>, unlike fitting
    /// rules which stay item-level catalog links (<c>item_routing_rules</c>,
    /// World B). Readers follow <c>current_version_label</c>, so a rollback
    /// restores the activated version's own ranges. NULL min/max =
    /// unrestricted criterion.
    /// </summary>
    public const string MigrateV38AddSegmentRules = """
        CREATE TABLE IF NOT EXISTS family_segment_rules (
            catalog_version_id TEXT NOT NULL,
            family_key TEXT NOT NULL DEFAULT '',
            type_name TEXT NOT NULL,
            rule_order INTEGER NOT NULL,
            segment_name TEXT NOT NULL,
            min_size_feet REAL,
            max_size_feet REAL,
            description TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (catalog_version_id, family_key, type_name, rule_order),
            FOREIGN KEY (catalog_version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_family_segment_rules_version ON family_segment_rules (catalog_version_id);
        """;

    /// <summary>
    /// V34 (#254, ADR-072): routing rules of system MEPCurve types as
    /// catalog DATA — the mini-project no longer carries fittings, so the
    /// routing of a version lives here instead of the staged .rvt.
    /// <c>group_key</c> is the string group identity
    /// (<see cref="RoutingGroupKeys"/>: manager group name or
    /// <c>"Param:&lt;BIP&gt;"</c>); <c>part_name</c> NULL = no-part rule
    /// ("Нет"); <c>criteria_json</c> preserves arbitrary criterion kinds.
    /// Scoping mirrors <c>family_types</c>: (family_key, type_name) —
    /// one category item can hold same-named types of different system
    /// families (FHV6).
    /// </summary>
    public const string CreateFamilyRoutingRules = """
        CREATE TABLE IF NOT EXISTS family_routing_rules (
            catalog_item_id TEXT NOT NULL,
            catalog_version_id TEXT NOT NULL,
            family_key TEXT NOT NULL DEFAULT '',
            type_name TEXT NOT NULL,
            group_key TEXT NOT NULL,
            rule_order INTEGER NOT NULL,
            part_name TEXT,
            description TEXT NOT NULL DEFAULT '',
            criteria_json TEXT NOT NULL DEFAULT '[]',
            PRIMARY KEY (catalog_version_id, family_key, type_name, group_key, rule_order),
            FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
            FOREIGN KEY (catalog_version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE
        )
        """;

    /// <summary>
    /// V34 (#254, ADR-072): per-type routing scalars (PreferredJunctionType
    /// — manager types, or RBS_CURVETYPE_PREFERRED_BRANCH_PARAM — flex).
    /// Presence of a row doubles as the "this version's routing is stored
    /// as data" marker for the legacy fallback (a legitimately rule-less
    /// type keeps its settings row).
    /// </summary>
    public const string CreateFamilyRoutingTypeSettings = """
        CREATE TABLE IF NOT EXISTS family_routing_type_settings (
            catalog_version_id TEXT NOT NULL,
            family_key TEXT NOT NULL DEFAULT '',
            type_name TEXT NOT NULL,
            preferred_junction_type INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (catalog_version_id, family_key, type_name),
            FOREIGN KEY (catalog_version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE
        )
        """;

    public const string CreateFamilyRoutingRulesIndexes = """
        CREATE INDEX IF NOT EXISTS ix_family_routing_rules_item ON family_routing_rules (catalog_item_id);
        CREATE INDEX IF NOT EXISTS ix_family_routing_rules_version ON family_routing_rules (catalog_version_id)
        """;
}

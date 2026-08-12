namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Forward-compatibility constants for the FamilyManager catalog database
/// (ADR-058, #173).
/// </summary>
public static class DbCompatibility
{
    /// <summary>
    /// The oldest SmartCon version that can safely WRITE to a database
    /// carrying the current breaking data format. Bumped only by changes
    /// that make older plugins harmful to the catalog (e.g. a new
    /// content-hash format: their dedup would silently create duplicates).
    /// Every bump MUST ship with a backfill of
    /// <c>database_meta.min_plugin_version</c> from the data marker that
    /// proves the breaking change was applied (FHV3 precedent: schema
    /// migration V24 keyed on <c>hash_format_version = 3</c>; FHV4+:
    /// runtime backfill inside the hash actualization task —
    /// v4+ rows exist only after the task runs, so a schema migration
    /// cannot key on them).
    /// Non-breaking releases (additive columns, optional artifacts) do NOT
    /// bump this floor.
    /// FHV10 note (2026-08-12): the floor targets the next beta that first
    /// SHIPS the FHV8+ formats — v2.0.1-beta.8 (v8/v9 hashes never shipped
    /// in any release build; latest tag at the time: v2.0.1-beta.7). The
    /// constant must never exceed the version it ships in — a higher floor
    /// would self-gate the migrating build in release mode (in DEBUG
    /// builds the gate is off by default).
    /// </summary>
    public const string CurrentMinPluginVersion = "2.0.1-beta.8";
}

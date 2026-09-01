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
    /// SHIPS the FHV8+ formats - v2.0.1-beta.8 (v8/v9 hashes never shipped
    /// in any release build; latest tag at the time: v2.0.1-beta.7). The
    /// constant must never exceed the version it ships in - a higher floor
    /// would self-gate the migrating build in release mode (in DEBUG
    /// builds the gate is off by default).
    /// FHV11 note (2026-08-23, Issue #238): the floor targets the next beta
    /// that first SHIPS FHV11 - v2.0.1-beta.9 (FHV10 hashes never shipped
    /// in any release build; latest tag at the time: v2.0.1-beta.8). A
    /// pre-beta.9 plugin writing v11-format rows would mis-dedup (it does
    /// not know the LOOKUP section).
    /// FHV12 note (2026-08-27, Issue #249): the floor targets the next beta
    /// that first SHIPS FHV12 - v2.0.1-beta.10 (FHV11 hashes never shipped
    /// in any release build; latest tag at the time: v2.0.1-beta.9). A
    /// pre-beta.10 plugin writing v12-format rows would mis-dedup (it does
    /// not know the DEF section and the strengthened GEOM metrics).
    /// FHV13 note (2026-08-28, Issue #249): FHV12 never shipped in any
    /// release build (latest tag: v2.0.1-beta.9), so FHV13 ships in the
    /// SAME beta.10 and the floor stays unchanged — any released plugin
    /// writing v13-format rows must be ≥ beta.10 either way. FHV14 (same
    /// day, manual-test round 2) inherits the same reasoning: neither 12
    /// nor 13 ever shipped — only FHV14 will. FHV15 (manual-test round 3,
    /// deterministic reference type) and FHV16 (round 4, negative-zero
    /// canonicalization) — same: only FHV16 ships. FHV17 (round 5,
    /// canonical-order determinism: emitted-string sort keys) and FHV18
    /// (#251, per-face color histogram) — same: only FHV18 ships. FHV19
    /// (#254, param-based routing leaves VALUES) and FHV20 (World B, the
    /// ROUTING section leaves the system hash) — same: both shipped in the
    /// same feature branch before the next tag, so only FHV20 ships and the
    /// floor stays beta.10 (any released plugin writing v20-format rows is
    /// ≥ the tag that carries FHV12..20).
    /// </summary>
    public const string CurrentMinPluginVersion = "2.0.1-beta.10";
}

namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Why a family is considered stale (or not).
/// </summary>
public enum StaleReason
{
    /// <summary>Family is up-to-date (or has not been checked yet).</summary>
    None = 0,

    /// <summary>
    /// Family has no ExtensibleStorage marker (loaded before Phase 24,
    /// or loaded by an older plugin that did not write the marker).
    /// Treated as stale by default per Issue #69 AC.
    /// </summary>
    NoEntityStorage = 1,

    /// <summary>The version in ES differs from the catalog's current version.</summary>
    VersionMismatch = 2,

    /// <summary>Revit major version used to load the family differs from the current Revit.</summary>
    RevitVersionMismatch = 3,

    /// <summary>Family exists in the project but is not in the FamilyManager catalog.</summary>
    NotInCatalog = 4,

    /// <summary>
    /// #180: the ES marker matches the catalog's current version, but the
    /// FHV10 content proof shows the embedded/loaded copy was EDITED
    /// LOCALLY (type values, formulas, geometry, connectors) after the
    /// marker was written. Marker-only checks are blind to this drift.
    /// «Обновить» restores the catalog content (local edits are lost).
    /// </summary>
    ContentDrift = 5,

    /// <summary>
    /// ADR-072 World B: the ES marker matches the catalog, but the LIVE
    /// routing preferences of the loaded system type differ from the
    /// catalog's item-level routing links (the editor edited them, or the
    /// project type was reconfigured manually). Routing left the content
    /// hash, so the drift probe compares <c>RoutingFingerprint</c>s.
    /// «Обновить» applies the catalog routing to the project type.
    /// </summary>
    RoutingDrift = 6,
}

/// <summary>
/// Result of a stale check for a single family.
/// </summary>
/// <param name="CatalogItemId">Catalog item ID, or <c>null</c> if the family is not in the catalog.</param>
/// <param name="FamilyName">Family name (for UI display).</param>
/// <param name="CurrentVersionLabel">Version label in the catalog (if any).</param>
/// <param name="LoadedVersionLabel">Version label read from ES (if any).</param>
/// <param name="IsStale">True if the family should be updated.</param>
/// <param name="Reason">Why the family is stale (or <see cref="StaleReason.None"/>).</param>
public sealed record StaleCheckResult(
    string CatalogItemId,
    string FamilyName,
    string? CurrentVersionLabel,
    string? LoadedVersionLabel,
    bool IsStale,
    StaleReason Reason);

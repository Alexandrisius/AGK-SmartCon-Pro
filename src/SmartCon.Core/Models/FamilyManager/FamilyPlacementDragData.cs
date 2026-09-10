namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Payload for drag-and-drop family type placement from FamilyManager to Revit canvas.
/// </summary>
public sealed record FamilyPlacementDragData(
    string CatalogItemId,
    string FamilyName,
    string TypeName,
    int TargetRevitVersion,
    bool IsVirtual = false,
    string FamilySource = "loadable",
    string? UniqueId = null,
    /// <summary>Issue #183: Revit SYSTEM family of the type (not the catalog
    /// item display name in <see cref="FamilyName"/>) — the sync identity is
    /// (system family, type name). null for legacy/loadable.</summary>
    string? SystemFamilyName = null,
    /// <summary>Issue #190 (ADR-064): locale-invariant system family key —
    /// preferred over <see cref="SystemFamilyName"/> for matching.</summary>
    string? SystemFamilyKey = null,
    /// <summary>#210: the type is ALREADY in the project but outdated (ES
    /// marker does not match the catalog). The drop handler must NOT take
    /// the "already loaded" skip path — it reloads from the catalog so DnD
    /// never places a stale copy (freshness-on-place).</summary>
    bool IsStaleInProject = false);

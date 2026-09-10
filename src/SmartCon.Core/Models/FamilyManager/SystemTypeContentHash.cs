namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Content hash of one system-family type (Issue #249, Phase 2; the
/// content-grade side of #179). Returned as a list — NOT a dictionary —
/// because one <see cref="SystemFamilySnapshot"/> (a whole category) can
/// contain same-named types of different system families (FHV6: both
/// ducts are «Короб»), so a name-keyed map would silently drop entries.
/// Consumers key the entries via <see cref="SystemTypeIdentityKey.Build"/>
/// when they need a lookup map — the same key the stale detector and the
/// type repository already use.
/// </summary>
/// <param name="TypeName">Type name (user content).</param>
/// <param name="FamilyKey">Locale-invariant family identity when known
/// (#190, ADR-064); <c>null</c> for legacy snapshots.</param>
/// <param name="FamilyName">Revit system family display name; fallback
/// identity token when <paramref name="FamilyKey"/> is absent.</param>
/// <param name="HashHex">
/// SHA-256 hex of the type's full canonical body (name + values + FAMKEY
/// + STRUCT + ROUTING + SEGMENTS + SUBTYPES + RAILING + WIRE).
/// </param>
public sealed record SystemTypeContentHash(
    string TypeName,
    string? FamilyKey,
    string? FamilyName,
    string HashHex)
{
    /// <summary>
    /// The canonical identity key of this type row
    /// (<see cref="SystemTypeIdentityKey.Build"/>) — "TOKEN|NAME"
    /// upper-invariant, matching the stale detector's per-type map keys.
    /// </summary>
    public string IdentityKey => SystemTypeIdentityKey.Build(FamilyKey, FamilyName, TypeName);
}

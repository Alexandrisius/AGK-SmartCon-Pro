namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One canonical section of the family content identity (Issue #249,
/// Phase 1): the section name (<see cref="FamilyContentSectionNames"/>),
/// its canonical substring exactly as embedded in the full canonical
/// string (with the leading section marker), and the section's own
/// SHA-256 hex. Concatenating the <see cref="CanonicalString"/> of all
/// sections in canonical order reproduces the full canonical string
/// byte-for-byte — this is a guaranteed invariant covered by unit tests.
/// </summary>
/// <param name="SectionName">Section identifier (PARAMS, TYPES, GEOM, …).</param>
/// <param name="CanonicalString">
/// The exact substring this section contributes to the full canonical
/// string, including its leading marker (e.g. <c>PARAMS|…|</c>).
/// </param>
/// <param name="HashHex">SHA-256 hex (uppercase) of <see cref="CanonicalString"/>.</param>
/// <param name="TypeName">
/// For system families the canonical string interleaves per-type bodies,
/// so their section entries carry the owning type name; <c>null</c> for
/// loadable sections and for the META prefix.
/// </param>
public sealed record ContentSectionHash(
    string SectionName,
    string CanonicalString,
    string HashHex,
    string? TypeName = null);

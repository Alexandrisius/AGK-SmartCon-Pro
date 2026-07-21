namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Everything an actualization task needs to apply its artifacts for ONE
/// family group (ADR-054): the group identity/variants, the variant that
/// was opened, and the extraction products from the SINGLE open session
/// (snapshot + per-type geometry). Geometry is <c>null</c> when its
/// extraction failed — tasks that need it fall back to their own pass.
/// </summary>
public sealed record FamilyActualizationContext(
    ActualizationGroup Group,
    ActualizationVariant OpenedVariant,
    string AbsolutePath,
    FamilySnapshot Snapshot,
    IReadOnlyList<FamilyGeometryPerType>? Geometry);

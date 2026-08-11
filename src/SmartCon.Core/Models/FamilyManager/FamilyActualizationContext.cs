namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Everything an actualization task needs to apply its artifacts for ONE
/// family group (ADR-054): the group identity/variants, the variant that
/// was opened, and the extraction products from the SINGLE open session
/// (snapshot + per-type geometry). Geometry is <c>null</c> when its
/// extraction failed — tasks that need it fall back to their own pass.
/// <see cref="SystemSnapshot"/> is set only on the system path
/// (staged .rvt, ADR-056) — loadable groups carry <c>null</c>.
/// </summary>
public sealed record FamilyActualizationContext(
    ActualizationGroup Group,
    ActualizationVariant OpenedVariant,
    string AbsolutePath,
    FamilySnapshot Snapshot,
    IReadOnlyList<FamilyGeometryPerType>? Geometry,
    SystemFamilySnapshot? SystemSnapshot = null,
    IReadOnlyList<FamilySnapshot>? SharedNestedSnapshots = null,
    IReadOnlyList<SharedNestedSubtree>? SharedNestedSubtrees = null)
{
    private static readonly FamilySnapshot NoExtractionSnapshot = new(
        FamilyName: "(file-level task — engine extraction skipped)",
        Category: string.Empty,
        Parameters: [],
        Types: [],
        Geometry: new GeometryMetrics(0, []),
        SharedNestedFamilyNames: []);

    /// <summary>
    /// Context for groups whose pending tasks are ALL file-level
    /// (<see cref="Services.Interfaces.IDatabaseActualizationTask.RequiresExtraction"/>
    /// = false, e.g. <c>mini-project-marker-v1</c>): the engine did not open
    /// or extract the file, so <see cref="Snapshot"/> is a sentinel that must
    /// not be read. Also used when extraction failed but file-level tasks
    /// still apply (they are decoupled from the extraction subsystem).
    /// </summary>
    public static FamilyActualizationContext WithoutExtraction(
        ActualizationGroup group, ActualizationVariant openedVariant, string absolutePath)
        => new(group, openedVariant, absolutePath, NoExtractionSnapshot, null);
}

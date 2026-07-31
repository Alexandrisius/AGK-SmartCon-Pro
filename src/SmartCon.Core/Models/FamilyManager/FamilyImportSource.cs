namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// v2.0.0: Strongly-typed source payload for a <see cref="FamilyBatchImportItem"/>
/// row whose managed file is not yet on disk. The VM-side import flow now
/// produces "virtual" batch rows (placeholder <c>FilePath</c> like
/// <c>"system://OST_Pipes"</c> or <c>"loadable://FamilyName"</c>) so the
/// batch dialog can open within 1-2 seconds even for a 50-family project
/// (previously the flow called <c>CreateCleanProjectWithTypesAndInstances</c>
/// and <c>StageLoadableFamilyFromProject</c> BEFORE the dialog, which
/// blocked the UI for 20-30 seconds and left orphan files on cancel).
///
/// After the user confirms the dialog, the orchestrator reads the
/// <see cref="FamilyImportSource"/> back, calls the appropriate Revit-API
/// staging helper (<c>CreateCleanProjectWithTypesAndInstances</c> or
/// <c>StageLoadableFamilyFromProject</c>) on the post-dialog flow, and
/// writes the result into managed storage.
///
/// This record lives in <c>SmartCon.Core</c> so the public API of
/// <see cref="FamilyBatchImportItem"/> does not pull in
/// <c>Autodesk.Revit.DB</c>.
/// </summary>
public abstract record FamilyImportSource
{
    private FamilyImportSource() { }

    /// <summary>
    /// System-family source. Carries the type list and the category
    /// ordinal (the actual <c>BuiltInCategory</c> enum is reconstructed
    /// in the VM layer when staging).
    /// </summary>
    /// <param name="DisplayName">Display name of the category (e.g. "Трубы").</param>
    /// <param name="CategoryId">Numeric <c>BuiltInCategory</c> ordinal.</param>
    /// <param name="TypeUniqueIds">UniqueIds of the type elements to copy into the mini-rvt.</param>
    /// <param name="TypeNames">Display names of the same types (parallel to <paramref name="TypeUniqueIds"/>).</param>
    /// <param name="TypeFamilyNames">Revit system family of each type
    /// (parallel to <paramref name="TypeUniqueIds"/>; Issue #183) — flows
    /// into <c>family_types.family_name</c>; null for legacy producers.</param>
    public sealed record SystemSource(
        string DisplayName,
        int CategoryId,
        IReadOnlyList<string> TypeUniqueIds,
        IReadOnlyList<string> TypeNames,
        IReadOnlyList<string?>? TypeFamilyNames = null) : FamilyImportSource;

    /// <summary>
    /// Loadable-family source. The orchestrator's post-dialog flow calls
    /// <c>EditFamily</c> + <c>SaveAs</c> on this family to land a copy
    /// in managed storage.
    /// </summary>
    public sealed record LoadableSource(
        string FamilyName,
        string FamilyUniqueId,
        string CategoryName) : FamilyImportSource;
}

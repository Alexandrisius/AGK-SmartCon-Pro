namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// v2.0.0: the canonical
/// <c>(CatalogItemId, VersionLabel, ManagedPath)</c> triple that the
/// batch-import VM allocates up front and propagates through every stage
/// of the import flow (build → dialog → staging → import).
/// </summary>
/// <remarks>
/// This is the only data shape that satisfies the
/// <c>family_files.relative_path = "{dbRoot}/files/&lt;id&gt;/&lt;version&gt;/&lt;name&gt;"</c>
/// invariant: <see cref="ManagedPath"/> is the absolute on-disk path,
/// <see cref="CatalogItemId"/> and <see cref="VersionLabel"/> are the
/// parts that, together with the file-name, compose that layout.
/// <para>
/// The triple is immutable; the VM only ever replaces it (via
/// <see cref="IFamilyImportPrecomputer.BuildPrecomputedTripleAsync"/>) —
/// it never mutates fields in-place. That keeps the round-trip from
/// dialog back to import flow loss-free (no half-updated state where
/// the id is from the old name but the path is from the new one).
/// </para>
/// </remarks>
public sealed record PrecomputedImportTriple(
    string CatalogItemId,
    string VersionLabel,
    string ManagedPath);

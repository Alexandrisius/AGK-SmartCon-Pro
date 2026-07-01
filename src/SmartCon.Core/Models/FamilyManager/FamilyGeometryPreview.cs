namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Aggregated 3D geometry of a single family version — the result of
/// <c>IFamilyGeometryExtractor.ExtractAsync</c>. Pure C# value (I-09):
/// no Revit <c>GeometryObject</c> retained (I-05). Passed to
/// <c>IGlbWriter</c> which serializes it to a GLB file.
/// </summary>
/// <param name="CatalogItemId">Catalog item identifier (matches
/// <c>catalog_items.id</c>).</param>
/// <param name="VersionLabel">Version label this geometry belongs to
/// (matches <c>catalog_versions.version_label</c>). Used as the
/// <c>family_assets.version_label</c> when the GLB is registered as
/// an auto-extracted asset (ADR-042).</param>
/// <param name="FamilyName">Family display name (without <c>.rfa</c>
/// extension), used as the <c>family_assets.file_name</c> for the GLB.</param>
/// <param name="Meshes">All non-empty <see cref="MeshData"/> entries
/// extracted from the family document. May be empty when the family
/// has no visible geometry (the pipeline should then skip writing).</param>
public sealed record FamilyGeometryPreview(
    string CatalogItemId,
    string VersionLabel,
    string FamilyName,
    IReadOnlyList<MeshData> Meshes)
{
    /// <summary>Sum of triangle counts across all meshes.</summary>
    public int TotalTriangleCount => Meshes.Sum(m => m.TriangleCount);

    /// <summary>Sum of vertex counts across all meshes.</summary>
    public int TotalVertexCount => Meshes.Sum(m => m.VertexCount);

    /// <summary><c>true</c> when the family has no visible geometry.</summary>
    public bool IsEmpty => Meshes.Count == 0 || Meshes.All(m => m.IsEmpty);
}

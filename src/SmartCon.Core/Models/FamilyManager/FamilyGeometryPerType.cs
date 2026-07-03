namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// 3D geometry for a single family type. The result of
/// <see cref="Services.Interfaces.IFamilySnapshotExtractor.ExtractGeometryPerType"/>
/// and the unit passed to <see cref="Services.Interfaces.IFamilyGeometryPipeline"/>.
/// Pure C# value (I-09): no Revit <c>GeometryObject</c> retained (I-05).
/// </summary>
/// <param name="TypeName">Family type name (matches
/// <c>FamilyType.Name</c> from <c>FamilyManager</c>). Empty string
/// for families with no types.</param>
/// <param name="FamilyName">Family display name (without <c>.rfa</c>).</param>
/// <param name="Meshes">All non-empty <see cref="MeshData"/> entries
/// extracted for this type. May be empty.</param>
public sealed record FamilyGeometryPerType(
    string TypeName,
    string FamilyName,
    IReadOnlyList<MeshData> Meshes)
{
    /// <summary>Sum of triangle counts across all meshes.</summary>
    public int TotalTriangleCount => Meshes.Sum(m => m.TriangleCount);

    /// <summary>Sum of vertex counts across all meshes.</summary>
    public int TotalVertexCount => Meshes.Sum(m => m.VertexCount);

    /// <summary><c>true</c> when this type has no visible geometry.</summary>
    public bool IsEmpty => Meshes.Count == 0 || Meshes.All(m => m.IsEmpty);
}

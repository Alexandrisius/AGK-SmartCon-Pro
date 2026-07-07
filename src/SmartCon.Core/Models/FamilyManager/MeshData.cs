using System.Numerics;

namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Tessellated mesh extracted from a single Revit <c>Solid</c> or
/// <c>GeometryInstance</c>. Pure C# value-type structure — NO Revit
/// API references (I-09). The Revit extractor immediately serializes
/// into this shape so no <c>GeometryObject</c> is held between calls (I-05).
/// </summary>
/// <param name="Positions">Vertex positions as a flat float array
/// layout [x0, y0, z0, x1, y1, z1, …]. Length is always a multiple of 3.</param>
/// <param name="Normals">Optional vertex normals in the same layout as
/// <paramref name="Positions"/>. <c>null</c> when the source
/// <c>Revit.Mesh</c> did not produce normals (in that case the GLB
/// writer will emit positions-only and the viewer can compute flat
/// normals on load).</param>
/// <param name="Indices">Triangle indices into <paramref name="Positions"/>.
/// Length is always a multiple of 3 (3 indices per triangle, counter-clockwise
/// winding in Revit's right-handed coordinate system).</param>
/// <param name="DiffuseColor"><see cref="Vector4"/> RGBA diffuse color
/// in 0..1 range. MVP material model (ADR-042 out-of-scope: PBR textures,
/// metalness, roughness — Phase 2 work).</param>
/// <param name="NodeName">Human-readable node name used in the glTF
/// scene tree (usually the source <c>GenericForm</c> type name + id,
/// e.g. <c>"Extrusion_12345"</c>).</param>
public sealed record MeshData(
    float[] Positions,
    float[]? Normals,
    int[] Indices,
    Vector4 DiffuseColor,
    string NodeName)
{
    /// <summary>Number of vertices (length of <see cref="Positions"/> / 3).</summary>
    public int VertexCount => Positions.Length / 3;

    /// <summary>Number of triangles (length of <see cref="Indices"/> / 3).</summary>
    public int TriangleCount => Indices.Length / 3;

    /// <summary>
    /// Returns <c>true</c> if the mesh has no positions or no triangles.
    /// The extractor should skip such meshes (typically empty forms).
    /// </summary>
    public bool IsEmpty => Positions.Length < 3 || Indices.Length < 3;
}

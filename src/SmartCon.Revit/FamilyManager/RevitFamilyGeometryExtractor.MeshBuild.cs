using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class RevitFamilyGeometryExtractor
{
    /// <summary>
    /// Shared traversal counters for <see cref="CollectMeshWithMaterials"/>.
    /// One instance per top-level call; recursion accumulates into the same
    /// instance so the summary reports totals including nested geometry.
    /// </summary>
    private sealed class TraversalStats
    {
        public int SolidCount;
        public int InstanceCount;
        public int DirectMeshCount;
        public int SkippedEmptySolid;
        public int UnknownTypeCount;

        public bool HasAny =>
            SolidCount > 0 || InstanceCount > 0 || DirectMeshCount > 0 ||
            SkippedEmptySolid > 0 || UnknownTypeCount > 0;
    }

    /// <summary>
    /// Per-material accumulator. One instance per distinct
    /// <c>Face.MaterialElementId</c> encountered while traversing an element's
    /// geometry. Holds the vertex/index/normal buffers for a single material
    /// group so <see cref="ExtractMeshesFromElement"/> can emit one
    /// <see cref="MeshData"/> per material.
    /// </summary>
    /// <remarks>
    /// <b>No shared dedup dictionary:</b> vertex deduplication is performed
    /// per-<c>Face</c> inside <see cref="AddRevitMeshToGroup"/> (a fresh
    /// <c>Dictionary</c> per call), NOT stored on this group. This preserves
    /// hard edges between faces of the same material (e.g. a cylinder rim
    /// stays sharp) while keeping smooth shading within a single face. See
    /// <see cref="AddRevitMeshToGroup"/> doc for the Issue #108 regression
    /// rationale.
    /// </remarks>
    private sealed class MaterialMeshGroup
    {
        public ElementId MaterialId { get; }
        public int MaterialIndex { get; }
        public List<float> Positions { get; } = new();
        public List<int> Indices { get; } = new();
        public List<float> Normals { get; } = new();

        public MaterialMeshGroup(ElementId materialId, int index)
        {
            MaterialId = materialId;
            MaterialIndex = index;
        }
    }

    /// <summary>
    /// Recursively collect mesh triangles from a GeometryElement, grouping
    /// faces by <c>Face.MaterialElementId</c> into separate
    /// <see cref="MaterialMeshGroup"/> entries. Issue #108 fix: this replaces
    /// the single-buffer <c>CollectMesh</c> so each material becomes its own
    /// <see cref="MeshData"/> with its own <c>DiffuseColor</c>.
    /// <para>
    /// Handles:
    /// <list type="bullet">
    /// <item><description><see cref="Solid"/>: each <c>Face</c> is triangulated
    /// via <c>Face.Triangulate(1.0)</c> and appended to the group for that
    /// face's <c>MaterialElementId</c>.</description></item>
    /// <item><description><see cref="GeometryInstance"/>: recurse into
    /// <c>GetInstanceGeometry()</c> which applies the instance transform,
    /// placing nested-family geometry at its correct position within the
    /// parent family document. This is the critical fix for nested
    /// <c>FamilyInstance</c> — the old <c>GetColorFromGeometry</c> did not
    /// descend into <c>GeometryInstance</c>, so nested families always got
    /// <c>FallbackColor</c>. Jeremy Tammik (The Building Coder
    /// a/0026_instance_materials.htm) confirms the recursion pattern.</description></item>
    /// <item><description><see cref="Mesh"/>: direct meshes (imported geometry,
    /// topography-style) have no per-face material — appended to the
    /// <c>InvalidElementId</c> fallback group.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    private static void CollectMeshWithMaterials(
        GeometryElement geomElem,
        Dictionary<int, MaterialMeshGroup> groups,
        bool verbose,
        TraversalStats? stats,
        CancellationToken ct)
    {
        // Stats are shared across recursion so the top-level summary reports
        // totals including nested GeometryInstance content. Per-object Debug
        // lines were removed (smartcon.log bloat): Line/Arc objects are
        // expected in every GeometryElement enumeration and the counters +
        // the top-level summary carry the same information.
        var isTopLevel = stats is null;
        stats ??= new TraversalStats();

        foreach (var geomObj in geomElem)
        {
            ct.ThrowIfCancellationRequested();

            switch (geomObj)
            {
                case Solid solid:
                    if (solid.SurfaceArea <= 0)
                    {
                        stats.SkippedEmptySolid++;
                        break;
                    }
                    AddSolidWithMaterials(solid, groups);
                    stats.SolidCount++;
                    break;

                case GeometryInstance geomInst:
                    var instanceGeom = geomInst.GetInstanceGeometry();
                    if (instanceGeom is not null)
                    {
                        CollectMeshWithMaterials(instanceGeom, groups, verbose, stats, ct);
                        stats.InstanceCount++;
                    }
                    break;

                case Mesh directMesh:
                    AddDirectMeshToGroup(directMesh, groups);
                    stats.DirectMeshCount++;
                    break;

                default:
                    stats.UnknownTypeCount++;
                    break;
            }
        }

        // Summary only at the top-level call: the method recurses per
        // GeometryInstance, and logging per recursion level produced one
        // summary line per nested instance.
        if (isTopLevel && verbose && stats.HasAny)
        {
            var totalVerts = groups.Values.Sum(g => g.Positions.Count / 3);
            var totalTris = groups.Values.Sum(g => g.Indices.Count / 3);
            SmartConLogger.Debug(
                $"    CollectMeshWithMaterials: {stats.SolidCount} solids, {stats.InstanceCount} geometry instances, " +
                $"{stats.DirectMeshCount} direct meshes, {stats.SkippedEmptySolid} empty solids skipped, " +
                $"{stats.UnknownTypeCount} unknown types skipped → {groups.Count} material group(s), " +
                $"{totalVerts} verts, {totalTris} tris");
        }

        // Issue #99 fix: renormalise accumulated vertex normals per group so
        // the GLB consumer (HelixToolkit Phong shader) interprets them as
        // standard glTF normals. Per-group normalisation means smooth shading
        // within a material and hard edges between materials (vertices are not
        // shared between groups — each group has its own dedup dictionary).
        foreach (var group in groups.Values)
        {
            if (group.Positions.Count > 0)
            {
                NormalizeVertexNormals(group.Positions, group.Normals);
            }
        }
    }

    /// <summary>
    /// Triangulates each face of a solid via <c>Face.Triangulate(1.0)</c> and
    /// appends the triangles to the <see cref="MaterialMeshGroup"/> matching
    /// the face's <c>MaterialElementId</c>. Issue #108 fix: this replaces the
    /// single-buffer <c>AddSolid</c> so faces with different materials end up
    /// in separate groups (and thus separate <see cref="MeshData"/> entries).
    /// </summary>
    /// <remarks>
    /// <b>Tessellation choice (Issue #99):</b> <c>SolidUtils.TessellateSolidOrShell</c>
    /// with <c>LevelOfDetail=1.0</c> produces too few axial segments for small
    /// cylinders (10-20mm diameter) due to Revit's internal area-scaled
    /// tessellation heuristic. Per-face <c>Face.Triangulate(1.0)</c> gives
    /// consistent high-density tessellation regardless of solid size (1747 tris
    /// for the 16mm pipe bend vs 412 from <c>TessellateSolidOrShell</c>).
    /// Per-face triangulation also gives shared vertices within a material
    /// group that the dedup pass + <c>AccumulateTriangleNormal</c> +
    /// <c>NormalizeVertexNormals</c> pipeline turns into smooth (Gouraud-style)
    /// normals across face boundaries inside the same material.
    /// </remarks>
    private static void AddSolidWithMaterials(
        Solid solid, Dictionary<int, MaterialMeshGroup> groups)
    {
        if (solid.Faces is null || solid.Faces.Size == 0) return;

        foreach (Face face in solid.Faces)
        {
            try
            {
                var mesh = face.Triangulate(1.0);
                if (mesh is null || mesh.NumTriangles == 0) continue;

                var materialId = face.MaterialElementId;
                var key = GetElementIdInt(materialId);
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new MaterialMeshGroup(materialId, groups.Count);
                    groups[key] = group;
                }
                AddRevitMeshToGroup(mesh, group);
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug($"    AddSolidWithMaterials: Face.Triangulate(1.0) failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Appends triangles from a Revit <see cref="Mesh"/> (from
    /// <c>Face.Triangulate</c>) into a single <see cref="MaterialMeshGroup"/>.
    /// Vertex normals are accumulated per-triangle (smooth shading within a
    /// face) and normalised in a final pass by
    /// <see cref="CollectMeshWithMaterials"/>.
    /// </summary>
    /// <remarks>
    /// <b>Per-face dedup (Issue #108 regression fix):</b> the dedup dictionary
    /// is created <b>fresh on every call</b> — one call corresponds to one
    /// Revit <c>Face</c>. Vertices on the seam between two faces (e.g. the
    /// rim of a cylinder cap meeting its side surface) are therefore
    /// <b>not</b> shared across faces even when they belong to the same
    /// material group. This preserves hard edges between faces (sharp cylinder
    /// rims) while keeping smooth (Gouraud) shading <i>within</i> a face.
    /// <para>
    /// The previous version of this method (during Issue #108 development)
    /// reused <c>group.Dedup</c> across all faces in a material group, which
    /// merged seam vertices and produced smooth shading across face
    /// boundaries — making cylinder rims look rounded and individual
    /// triangles visible (user-reported regression 2026-07-06). Restoring
    /// per-call dedup restores the pre-Issue-#108 visual quality while
    /// keeping the per-material grouping and color resolution fixes.
    /// </para>
    /// <para>
    /// <b>Vertex count note:</b> per-face dedup means a vertex on an N-face
    /// seam is stored N times (once per face) instead of once. This matches
    /// the pre-Issue-#108 behaviour and is the price of hard edges. Triangle
    /// count is unchanged.
    /// </para>
    /// </remarks>
    private static void AddRevitMeshToGroup(Mesh mesh, MaterialMeshGroup group)
    {
        var vertices = mesh.Vertices;
        var numTriangles = mesh.NumTriangles;
        if (vertices is null || vertices.Count == 0 || numTriangles == 0) return;

        var positions = group.Positions;
        var indices = group.Indices;
        var normals = group.Normals;
        var dedup = new Dictionary<string, int>(vertices.Count);

        for (int i = 0; i < numTriangles; i++)
        {
            MeshTriangle tri;
            try
            {
                tri = mesh.get_Triangle(i);
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug($"Mesh.get_Triangle({i}) failed: {ex.Message}");
                continue;
            }

            uint i0 = tri.get_Index(0);
            uint i1 = tri.get_Index(1);
            uint i2 = tri.get_Index(2);

            var v0 = vertices[(int)i0];
            var v1 = vertices[(int)i1];
            var v2 = vertices[(int)i2];

            int idx0 = AddVertex(v0, dedup, positions);
            int idx1 = AddVertex(v1, dedup, positions);
            int idx2 = AddVertex(v2, dedup, positions);

            AccumulateTriangleNormal(v0, v1, v2, idx0, idx1, idx2, positions, normals);

            indices.Add(idx0);
            indices.Add(idx1);
            indices.Add(idx2);
        }
    }

    /// <summary>
    /// Appends a direct <see cref="Mesh"/> (imported geometry, topography-style)
    /// to the <c>InvalidElementId</c> fallback group. Direct meshes carry no
    /// per-face material information, so they all land in the same group and
    /// receive <c>FallbackColor</c> via <see cref="GetColorForMaterialId"/>.
    /// </summary>
    private static void AddDirectMeshToGroup(
        Mesh mesh, Dictionary<int, MaterialMeshGroup> groups)
    {
        var key = GetElementIdInt(ElementId.InvalidElementId);
        if (!groups.TryGetValue(key, out var group))
        {
            group = new MaterialMeshGroup(ElementId.InvalidElementId, groups.Count);
            groups[key] = group;
        }
        AddRevitMeshToGroup(mesh, group);
    }

    /// <summary>
    /// Accumulates a triangle's face-normal into the per-vertex normal buffers.
    /// Issue #99 fix: previously this method OVERWROTE (assign '=') each shared
    /// vertex's normal with the triangle's face normal, producing flat shading —
    /// each visible facet stood out even at high triangle counts. Switching to
    /// accumulation ('+=') plus a final <see cref="NormalizeVertexNormals"/> pass
    /// produces Gouraud-style smooth shading across shared vertices.
    /// </summary>
    /// <remarks>
    /// Winding: Jeremy Tammik (The Building Coder) documents that Revit's
    /// tessellated mesh vertices are oriented consistently so the cross-product
    /// (v1-v0) × (v2-v0) always points outward from the solid. This guarantees
    /// that accumulating normals from neighbouring triangles averages compatible
    /// outward-facing vectors rather than producing "folded" or inverted normals.
    /// <para>
    /// Degenerate triangles (zero area; cross-product length ≤ 1e-12) are
    /// skipped entirely. The old fallback of "(0,0,1)" on degenerate triangles
    /// corrupted accumulated normals on shared vertices and produced rendering
    /// artefacts on edge seams.
    /// </para>
    /// </remarks>
    private static void AccumulateTriangleNormal(
        XYZ v0, XYZ v1, XYZ v2,
        int idx0, int idx1, int idx2,
        List<float> positions, List<float> normals)
    {
        var ax = v1.X - v0.X;
        var ay = v1.Y - v0.Y;
        var az = v1.Z - v0.Z;
        var bx = v2.X - v0.X;
        var by = v2.Y - v0.Y;
        var bz = v2.Z - v0.Z;
        var nx = ay * bz - az * by;
        var ny = az * bx - ax * bz;
        var nz = ax * by - ay * bx;
        var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len <= 1e-12)
        {
            // Degenerate triangle (zero area). Skip — do not corrupt the
            // accumulated normals of shared vertices with a fallback vector.
            return;
        }
        nx /= len; ny /= len; nz /= len;

        // Pad normals buffer lazily to match positions buffer length. Each new
        // vertex added via AddVertex pushes 3 floats into positions, so we grow
        // normals to the same length on first write to that index.
        while (normals.Count < positions.Count) normals.Add(0f);
        normals[idx0 * 3] += (float)nx; normals[idx0 * 3 + 1] += (float)ny; normals[idx0 * 3 + 2] += (float)nz;
        normals[idx1 * 3] += (float)nx; normals[idx1 * 3 + 1] += (float)ny; normals[idx1 * 3 + 2] += (float)nz;
        normals[idx2 * 3] += (float)nx; normals[idx2 * 3 + 1] += (float)ny; normals[idx2 * 3 + 2] += (float)nz;
    }

    /// <summary>
    /// Normalises every accumulated vertex normal to unit length. Called after
    /// all triangle contributions have been added via
    /// <see cref="AccumulateTriangleNormal"/>. Without this pass, vertices shared
    /// by many triangles carry an un-normalised sum-of-face-normals vector, which
    /// glTF viewers interpret as a non-unit normal — producing diffuse lighting
    /// brighter than intended (or NaN if magnitude is zero).
    /// </summary>
    private static void NormalizeVertexNormals(List<float> positions, List<float> normals)
    {
        // Pad to the same length as positions to cover vertices that were never
        // touched by a triangle (rare, but possible for orphan vertices created
        // during dedup races).
        while (normals.Count < positions.Count) normals.Add(0f);

        for (int i = 0; i < positions.Count; i += 3)
        {
            var nx = (double)normals[i];
            var ny = (double)normals[i + 1];
            var nz = (double)normals[i + 2];
            var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-12)
            {
                normals[i] = (float)(nx / len);
                normals[i + 1] = (float)(ny / len);
                normals[i + 2] = (float)(nz / len);
            }
            else
            {
                // Vertex with no contributing triangle (degenerate). Use a
                // safe default upward normal rather than leaving (0,0,0).
                normals[i] = 0f;
                normals[i + 1] = 0f;
                normals[i + 2] = 1f;
            }
        }
    }

    private static int AddVertex(XYZ vertex, Dictionary<string, int> dedup, List<float> positions)
    {
        var key = FormattableString.Invariant($"{vertex.X:0.##########}|{vertex.Y:0.##########}|{vertex.Z:0.##########}");
        if (dedup.TryGetValue(key, out var existing))
            return existing;

        var newIdx = positions.Count / 3;
        positions.Add((float)vertex.X);
        positions.Add((float)vertex.Y);
        positions.Add((float)vertex.Z);
        dedup[key] = newIdx;
        return newIdx;
    }
}

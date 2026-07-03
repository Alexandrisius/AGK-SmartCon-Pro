#if NET8_0_OR_GREATER
using System.IO;
using System.Numerics;
using SharpGLTF.Schema2;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.Geometry;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Geometry;

/// <summary>
/// Unit tests for <see cref="FamilyGeometryGlbWriter"/> — pure C# (no Revit,
/// no WPF). Validates GLB file round-trip: write a synthetic preview, re-load
/// via <see cref="ModelRoot.Load(string)"/>, verify mesh/triangle counts.
/// </summary>
public sealed class FamilyGeometryGlbWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FamilyGeometryGlbWriter _writer = new();

    public FamilyGeometryGlbWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smartcon_glb_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task WriteAsync_SimpleTriangle_WritesValidGlb()
    {
        // One triangle in the XY plane, normal facing +Z.
        var preview = new FamilyGeometryPreview(
            CatalogItemId: "test-item",
            VersionLabel: "v1",
            FamilyName: "TestFamily",
            Meshes: new[]
            {
                new MeshData(
                    Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                    Normals: new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: new Vector4(1, 0, 0, 1),
                    NodeName: "Tri1")
            });

        var path = Path.Combine(_tempDir, "triangle.glb");
        var ok = await _writer.WriteAsync(preview, path);

        Assert.True(ok);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);

        // Round-trip via SharpGLTF reader.
        var model = ModelRoot.Load(path);
        Assert.NotEmpty(model.LogicalMeshes);
        Assert.NotEmpty(model.LogicalScenes);

        var mesh = model.LogicalMeshes[0];
        Assert.NotEmpty(mesh.Primitives);
        var prim = mesh.Primitives[0];
        var posAccessor = prim.GetVertexAccessor("POSITION");
        Assert.NotNull(posAccessor);
        Assert.Equal(3, posAccessor.Count);
        var idxAccessor = prim.GetIndexAccessor();
        Assert.NotNull(idxAccessor);
        Assert.Equal(3, idxAccessor.Count);
    }

    [Fact]
    public async Task WriteAsync_MultipleMeshes_CreatesSeparatePrimitives()
    {
        var preview = new FamilyGeometryPreview(
            CatalogItemId: "test-item",
            VersionLabel: "v1",
            FamilyName: "TwoTriangles",
            Meshes: new[]
            {
                new MeshData(
                    Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                    Normals: null,
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: new Vector4(1, 0, 0, 1),
                    NodeName: "Tri1"),
                new MeshData(
                    Positions: new float[] { 5, 0, 0, 6, 0, 0, 5, 1, 0 },
                    Normals: null,
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: new Vector4(0, 0, 1, 1),
                    NodeName: "Tri2")
            });

        var path = Path.Combine(_tempDir, "two_meshes.glb");
        var ok = await _writer.WriteAsync(preview, path);

        Assert.True(ok);

        var model = ModelRoot.Load(path);
        Assert.Equal(2, model.LogicalMeshes.Count);
    }

    [Fact]
    public async Task WriteAsync_NoNormals_PositionsOnlyMesh()
    {
        var preview = new FamilyGeometryPreview(
            CatalogItemId: "test-item",
            VersionLabel: "v1",
            FamilyName: "PositionOnly",
            Meshes: new[]
            {
                new MeshData(
                    Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 },
                    Normals: null,
                    Indices: new int[] { 0, 1, 2, 1, 3, 2 },
                    DiffuseColor: new Vector4(0, 1, 0, 1),
                    NodeName: "Quad")
            });

        var path = Path.Combine(_tempDir, "no_normals.glb");
        var ok = await _writer.WriteAsync(preview, path);

        Assert.True(ok);

        var model = ModelRoot.Load(path);
        var prim = model.LogicalMeshes[0].Primitives[0];
        Assert.Equal(4, prim.GetVertexAccessor("POSITION")!.Count);
        Assert.Equal(6, prim.GetIndexAccessor()!.Count);  // 2 triangles → 6 indices
    }

    [Fact]
    public async Task WriteAsync_EmptyPreview_ReturnsFalseDoesNotWrite()
    {
        var preview = new FamilyGeometryPreview(
            CatalogItemId: "test-item",
            VersionLabel: "v1",
            FamilyName: "Empty",
            Meshes: Array.Empty<MeshData>());

        var path = Path.Combine(_tempDir, "empty.glb");
        var ok = await _writer.WriteAsync(preview, path);

        Assert.False(ok);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WriteAsync_AllMeshesEmpty_ReturnsFalseDoesNotWrite()
    {
        var preview = new FamilyGeometryPreview(
            CatalogItemId: "test-item",
            VersionLabel: "v1",
            FamilyName: "EmptyMeshes",
            Meshes: new[]
            {
                new MeshData(
                    Positions: Array.Empty<float>(),
                    Normals: Array.Empty<float>(),
                    Indices: Array.Empty<int>(),
                    DiffuseColor: Vector4.One,
                    NodeName: "Empty1")
            });

        var path = Path.Combine(_tempDir, "empty_meshes.glb");
        var ok = await _writer.WriteAsync(preview, path);

        Assert.False(ok);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WriteAsync_CubeTriangulation_WritesExpectedGeometry()
    {
        // 8 cube vertices, 12 triangles → 36 indices
        var cubeVerts = new[]
        {
            0f,0f,0f,  1f,0f,0f,  1f,1f,0f,  0f,1f,0f,  // front face
            0f,0f,1f,  1f,0f,1f,  1f,1f,1f,  0f,1f,1f   // back face
        };
        var cubeIndices = new[]
        {
            0,1,2, 0,2,3,  // front
            1,5,6, 1,6,2,  // right
            5,4,7, 5,7,6,  // back
            4,0,3, 4,3,7,  // left
            3,2,6, 3,6,7,  // top
            4,5,1, 4,1,0   // bottom
        };
        var flatNormals = new float[cubeVerts.Length];
        for (int i = 0; i < flatNormals.Length; i += 3)
            (flatNormals[i], flatNormals[i + 1], flatNormals[i + 2]) = (0, 0, 1);

        var preview = new FamilyGeometryPreview(
            CatalogItemId: "cube",
            VersionLabel: "v1",
            FamilyName: "Cube",
            Meshes: new[]
            {
                new MeshData(cubeVerts, flatNormals, cubeIndices, Vector4.One, "Cube")
            });

        var path = Path.Combine(_tempDir, "cube.glb");
        var ok = await _writer.WriteAsync(preview, path);

        Assert.True(ok);

        var model = ModelRoot.Load(path);
        var prim = model.LogicalMeshes[0].Primitives[0];
        Assert.Equal(8, prim.GetVertexAccessor("POSITION")!.Count);
        Assert.Equal(36, prim.GetIndexAccessor()!.Count);
    }

    [Fact]
    public async Task WriteAsync_CreatesParentDirectoryIfMissing()
    {
        var preview = new FamilyGeometryPreview(
            CatalogItemId: "item",
            VersionLabel: "v1",
            FamilyName: "F",
            Meshes: new[]
            {
                new MeshData(
                    new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                    null,
                    new int[] { 0, 1, 2 },
                    Vector4.One,
                    "T")
            });

        var path = Path.Combine(_tempDir, "new_folder", "sub_folder", "file.glb");
        var ok = await _writer.WriteAsync(preview, path);

        Assert.True(ok);
        Assert.True(File.Exists(path));
    }
}
#endif

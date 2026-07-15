using System.IO;
using System.Linq;
using System.Numerics;
using HelixToolkit.SharpDX.Assimp;
using HelixToolkit.SharpDX.Model.Scene;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.Geometry;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Geometry;

/// <summary>
/// Unit tests for <see cref="FamilyGeometryGlbWriter"/> — pure C# (no Revit,
/// no WPF). Validates GLB file round-trip: write a synthetic preview, re-load
/// via <see cref="HelixToolkit.SharpDX.Assimp.Importer"/>, and verify
/// mesh/triangle/material counts.
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
        var preview = CreateSimpleTrianglePreview();
        var path = Path.Combine(_tempDir, "triangle.glb");
        var ok = await _writer.WriteAsync(preview, path);

        Assert.True(ok);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);

        var root = LoadSceneRoot(path);
        Assert.NotNull(root);
        Assert.True(CountMeshNodes(root!) >= 1, "Expected at least one mesh node");
    }

    [Fact]
    public async Task WriteAsync_MultipleMeshes_CreatesSeparateMeshes()
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

        var root = LoadSceneRoot(path);
        Assert.NotNull(root);
        Assert.True(CountMeshNodes(root!) >= 2, "Expected at least two mesh nodes");
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

        var root = LoadSceneRoot(path);
        Assert.NotNull(root);
        Assert.True(CountMeshNodes(root!) >= 1, "Expected at least one mesh node");
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
        var cubeVerts = new[]
        {
            0f, 0f, 0f,  1f, 0f, 0f,  1f, 1f, 0f,  0f, 1f, 0f,
            0f, 0f, 1f,  1f, 0f, 1f,  1f, 1f, 1f,  0f, 1f, 1f
        };
        var cubeIndices = new[]
        {
            0, 1, 2,  0, 2, 3,
            1, 5, 6,  1, 6, 2,
            5, 4, 7,  5, 7, 6,
            4, 0, 3,  4, 3, 7,
            3, 2, 6,  3, 6, 7,
            4, 5, 1,  4, 1, 0
        };
        var flatNormals = new float[cubeVerts.Length];
        for (int i = 0; i < flatNormals.Length; i += 3)
        {
            flatNormals[i] = 0;
            flatNormals[i + 1] = 0;
            flatNormals[i + 2] = 1;
        }

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

        var root = LoadSceneRoot(path);
        Assert.NotNull(root);
        int triangleCount = CountTriangles(root!);
        Assert.True(triangleCount >= 12, $"Expected at least 12 triangles, got {triangleCount}");
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

    [Fact]
    public async Task WriteAsync_MultipleMeshesDifferentColors_PreservesMaterialColors()
    {
        var red = new Vector4(1f, 0f, 0f, 1f);
        var green = new Vector4(0f, 1f, 0f, 1f);
        var blue = new Vector4(0f, 0f, 1f, 1f);

        var preview = new FamilyGeometryPreview(
            CatalogItemId: "multi-color-item",
            VersionLabel: "v1",
            FamilyName: "MultiColorFamily",
            Meshes: new[]
            {
                new MeshData(
                    Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                    Normals: new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: red,
                    NodeName: "Pipe__mat0"),
                new MeshData(
                    Positions: new float[] { 2, 0, 0, 3, 0, 0, 2, 1, 0 },
                    Normals: new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: green,
                    NodeName: "Nut__mat1"),
                new MeshData(
                    Positions: new float[] { 4, 0, 0, 5, 0, 0, 4, 1, 0 },
                    Normals: new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: blue,
                    NodeName: "Handle__mat2")
            });

        var path = Path.Combine(_tempDir, "multi_color.glb");
        var ok = await _writer.WriteAsync(preview, path);

        Assert.True(ok);

        var root = LoadSceneRoot(path);
        Assert.NotNull(root);
        var colors = CollectMaterialColors(root!).ToList();
        Assert.True(colors.Count >= 3, $"Expected at least 3 material colors, got {colors.Count}");

        Assert.Contains(colors, c => ColorMatches(c, red));
        Assert.Contains(colors, c => ColorMatches(c, green));
        Assert.Contains(colors, c => ColorMatches(c, blue));
    }

    [Fact]
    public async Task WriteAsync_FallbackGrayColor_PreservesBaseColor()
    {
        var fallbackGray = new Vector4(0.65f, 0.65f, 0.65f, 1f);

        var preview = new FamilyGeometryPreview(
            CatalogItemId: "gray-item",
            VersionLabel: "v1",
            FamilyName: "GrayFamily",
            Meshes: new[]
            {
                new MeshData(
                    Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                    Normals: null,
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: fallbackGray,
                    NodeName: "NoMaterial")
            });

        var path = Path.Combine(_tempDir, "gray.glb");
        var ok = await _writer.WriteAsync(preview, path);

        Assert.True(ok);

        var root = LoadSceneRoot(path);
        Assert.NotNull(root);
        var colors = CollectMaterialColors(root!).ToList();
        Assert.NotEmpty(colors);
        Assert.Contains(colors, c => ColorMatches(c, fallbackGray));
    }

    private static SceneNode? LoadSceneRoot(string path)
    {
        using var importer = new Importer();
        var scene = importer.Load(path);
        return scene?.Root;
    }

    private static int CountMeshNodes(SceneNode root)
    {
        int count = 0;
        CountMeshNodesRecursive(root, ref count);
        return count;
    }

    private static void CountMeshNodesRecursive(SceneNode node, ref int count)
    {
        if (node is MeshNode)
            count++;
        foreach (var child in node.Items)
            CountMeshNodesRecursive(child, ref count);
    }

    private static int CountTriangles(SceneNode root)
    {
        int count = 0;
        CountTrianglesRecursive(root, ref count);
        return count;
    }

    private static void CountTrianglesRecursive(SceneNode node, ref int count)
    {
        if (node is MeshNode mesh && mesh.Geometry?.Indices is { Count: >= 3 } indices)
        {
            count += indices.Count / 3;
        }
        foreach (var child in node.Items)
            CountTrianglesRecursive(child, ref count);
    }

    private static IEnumerable<Vector4> CollectMaterialColors(SceneNode root)
    {
        return CollectMaterialColorsRecursive(root);
    }

    private static IEnumerable<Vector4> CollectMaterialColorsRecursive(SceneNode node)
    {
        if (node is MeshNode mesh && mesh.Material is not null)
        {
            var color = TryGetMaterialColor(mesh.Material);
            if (color.HasValue)
                yield return color.Value;
        }
        foreach (var child in node.Items)
        {
            foreach (var color in CollectMaterialColorsRecursive(child))
                yield return color;
        }
    }

    private static Vector4? TryGetMaterialColor(object material)
    {
        var typeName = material.GetType().Name;
        dynamic d = material;
        try
        {
            if (typeName == "PhongMaterialCore")
            {
                var c = d.DiffuseColor;
                return new Vector4((float)c.Red, (float)c.Green, (float)c.Blue, (float)c.Alpha);
            }
            if (typeName == "PBRMaterialCore")
            {
                var c = d.AlbedoColor;
                return new Vector4((float)c.Red, (float)c.Green, (float)c.Blue, (float)c.Alpha);
            }
        }
        catch
        {
            // ignore material types we don't know how to read
        }
        return null;
    }

    private static bool ColorMatches(Vector4 actual, Vector4 expected)
    {
        return Math.Abs(actual.X - expected.X) < 0.01f
            && Math.Abs(actual.Y - expected.Y) < 0.01f
            && Math.Abs(actual.Z - expected.Z) < 0.01f
            && Math.Abs(actual.W - expected.W) < 0.01f;
    }

    private static FamilyGeometryPreview CreateSimpleTrianglePreview() => new(
        CatalogItemId: "tri-item",
        VersionLabel: "v1",
        FamilyName: "TriFamily",
        Meshes: new[]
        {
            new MeshData(
                Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                Normals: new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                Indices: new int[] { 0, 1, 2 },
                DiffuseColor: new Vector4(1, 0, 0, 1),
                NodeName: "Triangle")
        });
}

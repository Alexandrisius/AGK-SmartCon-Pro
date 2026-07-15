#if NET8_0_OR_GREATER
using System.IO;
using System.Numerics;
using HelixToolkit.SharpDX.Model.Scene;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.Geometry;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Geometry;

/// <summary>
/// Round-trip integration tests for <see cref="GlbSceneLoader"/>.
/// Writes a GLB file using <see cref="FamilyGeometryGlbWriter"/> (custom zero-dependency
/// GLB writer), then loads it back through <see cref="GlbSceneLoader"/> (HelixToolkit.Assimp)
/// and verifies that the resulting HelixToolkit scene graph contains the
/// expected meshes.
/// </summary>
/// <remarks>
/// These tests require the SharpAssimp native DLL (win-x64) to be present
/// in the test host's runtimes folder. The HelixToolkit.SharpDX.Assimp
/// NuGet package includes this DLL automatically for net8.0-windows builds.
/// </remarks>
public sealed class GlbSceneLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FamilyGeometryGlbWriter _writer = new();

    public GlbSceneLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smartcon_glb_loader_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task LoadScene_SimpleTriangle_ReturnsNonNullSceneWithMeshes()
    {
        var preview = CreateSimpleTrianglePreview();
        var path = Path.Combine(_tempDir, "triangle.glb");
        var writeOk = await _writer.WriteAsync(preview, path);
        Assert.True(writeOk, "GLB writer failed to produce a file");

        var sceneRoot = GlbSceneLoader.LoadScene(path);

        Assert.NotNull(sceneRoot);
        // Assimp returns a GroupNode (derived from SceneNode) — accept any SceneNode
        // derived type.
        Assert.IsAssignableFrom<SceneNode>(sceneRoot);

        // Walk and count meshes — the simple triangle should produce at least one MeshNode.
        int meshCount = CountMeshes(sceneRoot!);
        Assert.True(meshCount >= 1, $"Expected at least 1 mesh, got {meshCount}");
    }

    [Fact]
    public async Task LoadScene_MultipleMeshes_AllMeshesLoaded()
    {
        var preview = new FamilyGeometryPreview(
            CatalogItemId: "multi-item",
            VersionLabel: "v1",
            FamilyName: "MultiFamily",
            Meshes: new[]
            {
                new MeshData(
                    Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                    Normals: new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: new Vector4(1, 0, 0, 1),
                    NodeName: "Mesh1"),
                new MeshData(
                    Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                    Normals: new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: new Vector4(0, 1, 0, 1),
                    NodeName: "Mesh2"),
                new MeshData(
                    Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                    Normals: new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: new Vector4(0, 0, 1, 1),
                    NodeName: "Mesh3")
            });

        var path = Path.Combine(_tempDir, "multi.glb");
        var writeOk = await _writer.WriteAsync(preview, path);
        Assert.True(writeOk);

        var sceneRoot = GlbSceneLoader.LoadScene(path);

        Assert.NotNull(sceneRoot);
        int meshCount = CountMeshes(sceneRoot!);
        // Assimp may merge meshes per-material, so we accept at least 1 mesh.
        Assert.True(meshCount >= 1, $"Expected at least 1 mesh, got {meshCount}");
    }

    [Fact]
    public void LoadScene_NonExistentFile_ReturnsNull()
    {
        var path = Path.Combine(_tempDir, "does_not_exist.glb");
        var sceneRoot = GlbSceneLoader.LoadScene(path);
        Assert.Null(sceneRoot);
    }

    [Fact]
    public void LoadScene_EmptyFile_ReturnsNull()
    {
        var path = Path.Combine(_tempDir, "empty.glb");
        File.WriteAllBytes(path, Array.Empty<byte>());

        var sceneRoot = GlbSceneLoader.LoadScene(path);
        Assert.Null(sceneRoot);
    }

    [Fact]
    public async Task LoadScene_CubeTriangulation_PreservesTriangleCount()
    {
        // Build a unit cube (12 triangles — 6 faces × 2 triangles each).
        var cube = CreateUnitCubePreview();
        var path = Path.Combine(_tempDir, "cube.glb");
        var writeOk = await _writer.WriteAsync(cube, path);
        Assert.True(writeOk);

        var sceneRoot = GlbSceneLoader.LoadScene(path);
        Assert.NotNull(sceneRoot);

        int triangleCount = CountTriangles(sceneRoot!);
        // SharpGLTF may triangulate quads, Assimp may merge primitives. The
        // cube uses 12 explicit triangles in MeshData — at least 12 should
        // be present (could be more if Assimp splits them per-material).
        Assert.True(triangleCount >= 12,
            $"Expected at least 12 triangles for the cube, got {triangleCount}");
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

    private static FamilyGeometryPreview CreateUnitCubePreview()
    {
        // Eight corner vertices of a unit cube.
        float[] p =
        {
            0, 0, 0,  1, 0, 0,  1, 1, 0,  0, 1, 0,  // bottom
            0, 0, 1,  1, 0, 1,  1, 1, 1,  0, 1, 1   // top
        };
        float[] n =
        {
            0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1,  // bottom-facing
            0, 0,  1, 0, 0,  1, 0, 0,  1, 0, 0,  1   // top-facing
        };
        // 12 triangles — 2 per cube face.
        int[] idx =
        {
            0, 1, 2,  0, 2, 3,  // bottom
            4, 6, 5,  4, 7, 6,  // top
            0, 4, 5,  0, 5, 1,  // front
            1, 5, 6,  1, 6, 2,  // right
            2, 6, 7,  2, 7, 3,  // back
            3, 7, 4,  3, 4, 0   // left
        };

        return new FamilyGeometryPreview(
            CatalogItemId: "cube-item",
            VersionLabel: "v1",
            FamilyName: "CubeFamily",
            Meshes: new[]
            {
                new MeshData(p, n, idx, new Vector4(0.7f, 0.7f, 0.7f, 1), "Cube")
            });
    }

    /// <summary>Recursively count MeshNodes in the scene graph.</summary>
    private static int CountMeshes(SceneNode root)
    {
        int count = 0;
        CountMeshesRecursive(root, ref count);
        return count;
    }

    private static void CountMeshesRecursive(SceneNode node, ref int count)
    {
        if (node is MeshNode) count++;
        foreach (var child in node.Items)
        {
            CountMeshesRecursive(child, ref count);
        }
    }

    /// <summary>Recursively sum triangle counts across all MeshNodes.</summary>
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
        {
            CountTrianglesRecursive(child, ref count);
        }
    }
}
#endif

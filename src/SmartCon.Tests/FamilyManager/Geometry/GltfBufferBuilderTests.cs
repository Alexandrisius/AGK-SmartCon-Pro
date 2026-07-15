using System.Linq;
using System.Numerics;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.Geometry;
using SmartCon.FamilyManager.Services.Geometry.Gltf;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Geometry;

/// <summary>
/// Unit tests for the internal <see cref="GltfBufferBuilder"/> — pure logic
/// (no Revit, no WPF, no GLB file I/O). Verifies buffer layout, accessor
/// component types, and alignment.
/// </summary>
public sealed class GltfBufferBuilderTests
{
    [Fact]
    public void Build_SmallMesh_UsesUnsignedShortIndices()
    {
        var preview = new FamilyGeometryPreview(
            CatalogItemId: "item",
            VersionLabel: "v1",
            FamilyName: "Small",
            Meshes: new[]
            {
                new MeshData(
                    Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                    Normals: null,
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: Vector4.One,
                    NodeName: "Tri")
            });

        var (model, buffer) = GltfBufferBuilder.Build(preview);

        var indexAccessor = model.Accessors[model.Accessors.Count - 1];
        Assert.Equal(GltfConstants.ComponentTypeUnsignedShort, indexAccessor.ComponentType);
        Assert.Equal(GltfConstants.TypeScalar, indexAccessor.Type);
        Assert.Equal(3, indexAccessor.Count);
        Assert.Equal(buffer.Length, model.Buffers[0].ByteLength);
    }

    [Fact]
    public void Build_LargeMesh_UsesUnsignedIntIndices()
    {
        // 65536 vertices forces UNSIGNED_INT indices.
        int vertexCount = ushort.MaxValue + 1;
        var positions = new float[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            positions[i * 3] = i;
            positions[i * 3 + 1] = 0;
            positions[i * 3 + 2] = 0;
        }
        var indices = new int[] { 0, 1, 2 };

        var preview = new FamilyGeometryPreview(
            CatalogItemId: "item",
            VersionLabel: "v1",
            FamilyName: "Large",
            Meshes: new[]
            {
                new MeshData(
                    Positions: positions,
                    Normals: null,
                    Indices: indices,
                    DiffuseColor: Vector4.One,
                    NodeName: "BigTri")
            });

        var (model, buffer) = GltfBufferBuilder.Build(preview);

        var indexAccessor = model.Accessors[model.Accessors.Count - 1];
        Assert.Equal(GltfConstants.ComponentTypeUnsignedInt, indexAccessor.ComponentType);
        Assert.Equal(GltfConstants.TypeScalar, indexAccessor.Type);
        Assert.Equal(3, indexAccessor.Count);
        Assert.Equal(buffer.Length, model.Buffers[0].ByteLength);
    }

    [Fact]
    public void Build_PositionsAccessor_HasBounds()
    {
        var preview = new FamilyGeometryPreview(
            CatalogItemId: "item",
            VersionLabel: "v1",
            FamilyName: "Bounds",
            Meshes: new[]
            {
                new MeshData(
                    Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                    Normals: null,
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: Vector4.One,
                    NodeName: "Tri")
            });

        var (model, _) = GltfBufferBuilder.Build(preview);

        var posAccessor = model.Accessors[0];
        Assert.Equal(GltfConstants.TypeVec3, posAccessor.Type);
        Assert.NotNull(posAccessor.Min);
        Assert.NotNull(posAccessor.Max);
        Assert.Equal(0.0f, posAccessor.Min![0]);
        Assert.Equal(1.0f, posAccessor.Max![0]);
    }

    [Fact]
    public void Build_BufferViews_AreAligned()
    {
        var preview = new FamilyGeometryPreview(
            CatalogItemId: "item",
            VersionLabel: "v1",
            FamilyName: "Align",
            Meshes: new[]
            {
                new MeshData(
                    Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                    Normals: new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: Vector4.One,
                    NodeName: "Tri")
            });

        var (model, _) = GltfBufferBuilder.Build(preview);

        foreach (var bv in model.BufferViews)
        {
            Assert.Equal(0, bv.ByteOffset % 4);
        }
    }

    [Fact]
    public void Build_RootNode_HasChildrenAndMatrix()
    {
        var preview = new FamilyGeometryPreview(
            CatalogItemId: "item",
            VersionLabel: "v1",
            FamilyName: "Family",
            Meshes: new[]
            {
                new MeshData(
                    Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                    Normals: null,
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: Vector4.One,
                    NodeName: "Tri")
            });

        var (model, _) = GltfBufferBuilder.Build(preview);

        var root = model.Nodes[0];
        Assert.Equal("Family", root.Name);
        Assert.NotNull(root.Matrix);
        Assert.Equal(16, root.Matrix!.Count);
        Assert.NotNull(root.Children);
        Assert.Single(root.Children!);
    }

    [Fact]
    public void Build_MeshWithNormals_HasNormalAccessor()
    {
        var preview = new FamilyGeometryPreview(
            CatalogItemId: "item",
            VersionLabel: "v1",
            FamilyName: "Normals",
            Meshes: new[]
            {
                new MeshData(
                    Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                    Normals: new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: Vector4.One,
                    NodeName: "Tri")
            });

        var (model, _) = GltfBufferBuilder.Build(preview);

        var primitive = model.Meshes[0].Primitives[0];
        Assert.True(primitive.Attributes.Normal.HasValue);
    }

    [Fact]
    public void Build_RootNode_Matrix_MapsRevitZUpToGltfYUp()
    {
        var preview = new FamilyGeometryPreview(
            CatalogItemId: "item",
            VersionLabel: "v1",
            FamilyName: "Family",
            Meshes: new[]
            {
                new MeshData(
                    Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                    Normals: null,
                    Indices: new int[] { 0, 1, 2 },
                    DiffuseColor: Vector4.One,
                    NodeName: "Tri")
            });

        var (model, _) = GltfBufferBuilder.Build(preview);

        var root = model.Nodes[0];
        Assert.NotNull(root.Matrix);
        var m = root.Matrix!;

        // glTF matrix is column-major. A column of a rotation matrix tells where
        // the corresponding basis vector of the source space is mapped. The -90°
        // rotation around X must send Revit +Z up to glTF +Y up, and Revit +Y
        // forward to glTF -Z. Values may contain tiny floating-point noise (~1e-6).
        Assert.Equal(1.0f, m[0], 5);   // column 1, row 1: X -> +X
        Assert.Equal(0.0f, m[1], 5);   // column 1, row 2: X -> 0Y
        Assert.Equal(0.0f, m[2], 5);   // column 1, row 3: X -> 0Z

        Assert.Equal(0.0f, m[4], 5);   // column 2, row 1: Y -> 0X
        Assert.Equal(0.0f, m[5], 5);   // column 2, row 2: Y -> 0Y
        Assert.Equal(-1.0f, m[6], 5);  // column 2, row 3: Y -> -Z

        Assert.Equal(0.0f, m[8], 5);   // column 3, row 1: Z -> 0X
        Assert.Equal(1.0f, m[9], 5);   // column 3, row 2: Z -> +Y
        Assert.Equal(0.0f, m[10], 5);  // column 3, row 3: Z -> 0Z
    }
}

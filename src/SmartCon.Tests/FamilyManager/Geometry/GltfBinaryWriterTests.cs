using System.IO;
using System.Numerics;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.Geometry.Gltf;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Geometry;

public sealed class GltfBinaryWriterTests
{
    [Fact]
    public void Write_GlbHeader_IsValid()
    {
        var json = "{}";
        var buffer = new byte[] { 0x00, 0x00, 0x00 };
        var glb = GltfBinaryWriter.Write(json, buffer);

        using var ms = new MemoryStream(glb);
        using var reader = new BinaryReader(ms);

        Assert.Equal(0x46546C67u, reader.ReadUInt32());
        Assert.Equal(2u, reader.ReadUInt32());
        var totalLength = reader.ReadUInt32();
        Assert.Equal((uint)glb.Length, totalLength);
    }

    [Fact]
    public void Write_ChunkLengths_IncludePadding()
    {
        var json = "x";
        var buffer = new byte[] { 1 };
        var glb = GltfBinaryWriter.Write(json, buffer);

        using var ms = new MemoryStream(glb);
        using var reader = new BinaryReader(ms);

        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();

        uint jsonChunkLength = reader.ReadUInt32();
        uint jsonChunkType = reader.ReadUInt32();
        Assert.Equal(0x4E4F534Au, jsonChunkType);
        Assert.Equal(4u, jsonChunkLength);

        reader.ReadBytes((int)jsonChunkLength);

        uint binChunkLength = reader.ReadUInt32();
        uint binChunkType = reader.ReadUInt32();
        Assert.Equal(0x004E4942u, binChunkType);
        Assert.Equal(4u, binChunkLength);
    }

    [Fact]
    public void Write_RealTriangle_ChunkLengthsArePaddedAndAligned()
    {
        var preview = new FamilyGeometryPreview(
            CatalogItemId: "item",
            VersionLabel: "v1",
            FamilyName: "Tri",
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
        var json = GltfJsonSerializer.Serialize(model);
        var glb = GltfBinaryWriter.Write(json, buffer);

        using var ms = new MemoryStream(glb);
        using var reader = new BinaryReader(ms);

        reader.ReadUInt32();
        reader.ReadUInt32();
        var totalLength = reader.ReadUInt32();

        uint jsonChunkLength = reader.ReadUInt32();
        uint jsonChunkType = reader.ReadUInt32();
        Assert.Equal(0x4E4F534Au, jsonChunkType);
        Assert.Equal(0u, jsonChunkLength % 4);

        reader.ReadBytes((int)jsonChunkLength);

        uint binChunkLength = reader.ReadUInt32();
        uint binChunkType = reader.ReadUInt32();
        Assert.Equal(0x004E4942u, binChunkType);
        Assert.Equal(0u, binChunkLength % 4);

        Assert.Equal((uint)glb.Length, totalLength);
        Assert.Equal(12u + 8u + jsonChunkLength + 8u + binChunkLength, totalLength);
    }
}

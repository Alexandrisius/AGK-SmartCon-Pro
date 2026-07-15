using System.IO;
using System.Text;

namespace SmartCon.FamilyManager.Services.Geometry.Gltf;

internal static class GltfBinaryWriter
{
    private const uint GltfMagic = 0x46546C67;
    private const uint GltfVersion = 2;
    private const uint JsonChunkType = 0x4E4F534A;
    private const uint BinChunkType = 0x004E4942;

    public static byte[] Write(string json, byte[] buffer)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPadding = (4 - jsonBytes.Length % 4) % 4;
        int binPadding = (4 - buffer.Length % 4) % 4;

        int totalLength = 12
            + 8 + jsonBytes.Length + jsonPadding
            + 8 + buffer.Length + binPadding;

        var result = new byte[totalLength];
        using (var writer = new BinaryWriter(new MemoryStream(result), Encoding.UTF8))
        {
            writer.Write(GltfMagic);
            writer.Write(GltfVersion);
            writer.Write((uint)totalLength);

            writer.Write((uint)(jsonBytes.Length + jsonPadding));
            writer.Write(JsonChunkType);
            writer.Write(jsonBytes);
            for (int i = 0; i < jsonPadding; i++)
                writer.Write((byte)0x20);

            writer.Write((uint)(buffer.Length + binPadding));
            writer.Write(BinChunkType);
            writer.Write(buffer);
            for (int i = 0; i < binPadding; i++)
                writer.Write((byte)0x00);
        }

        return result;
    }
}

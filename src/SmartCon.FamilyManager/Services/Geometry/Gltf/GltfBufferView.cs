namespace SmartCon.FamilyManager.Services.Geometry.Gltf;

internal sealed record GltfBufferView(
    int Buffer,
    int ByteOffset,
    int ByteLength,
    int Target);

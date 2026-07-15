namespace SmartCon.FamilyManager.Services.Geometry.Gltf;

internal sealed record GltfAccessor(
    int BufferView,
    int ByteOffset,
    int ComponentType,
    int Count,
    string Type,
    IReadOnlyList<float>? Min = null,
    IReadOnlyList<float>? Max = null);

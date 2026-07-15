namespace SmartCon.FamilyManager.Services.Geometry.Gltf;

internal sealed record GltfNode(
    string? Name = null,
    int? Mesh = null,
    IReadOnlyList<int>? Children = null,
    IReadOnlyList<float>? Matrix = null);

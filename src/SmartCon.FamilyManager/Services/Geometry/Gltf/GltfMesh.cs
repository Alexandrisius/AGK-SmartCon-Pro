namespace SmartCon.FamilyManager.Services.Geometry.Gltf;

internal sealed record GltfMesh(
    string Name,
    IReadOnlyList<GltfPrimitive> Primitives);

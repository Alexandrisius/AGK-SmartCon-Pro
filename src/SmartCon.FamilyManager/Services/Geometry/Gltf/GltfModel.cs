namespace SmartCon.FamilyManager.Services.Geometry.Gltf;

internal sealed record GltfModel(
    GltfAsset Asset,
    IReadOnlyList<GltfScene> Scenes,
    IReadOnlyList<GltfNode> Nodes,
    IReadOnlyList<GltfMesh> Meshes,
    IReadOnlyList<GltfAccessor> Accessors,
    IReadOnlyList<GltfBufferView> BufferViews,
    IReadOnlyList<GltfBuffer> Buffers,
    IReadOnlyList<GltfMaterial> Materials);

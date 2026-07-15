namespace SmartCon.FamilyManager.Services.Geometry.Gltf;

internal sealed record GltfPrimitive(
    GltfPrimitiveAttributes Attributes,
    int Indices,
    int Material,
    int Mode = GltfConstants.ModeTriangles);

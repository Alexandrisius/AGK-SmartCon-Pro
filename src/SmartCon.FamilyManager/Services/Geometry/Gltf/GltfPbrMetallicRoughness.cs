namespace SmartCon.FamilyManager.Services.Geometry.Gltf;

internal sealed record GltfPbrMetallicRoughness(
    IReadOnlyList<float> BaseColorFactor,
    float MetallicFactor = 0.0f,
    float RoughnessFactor = 0.5f);

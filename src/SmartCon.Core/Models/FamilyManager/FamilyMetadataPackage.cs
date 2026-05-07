using System.Text.Json.Serialization;

namespace SmartCon.Core.Models.FamilyManager;

public sealed class FamilyMetadataPackage
{
    [JsonPropertyName("format")]
    public string Format { get; init; } = "smartcon.familymanager.metadata-package";

    [JsonPropertyName("version")]
    public int Version { get; init; } = 2;

    [JsonPropertyName("exportedAtUtc")]
    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("sections")]
    public FamilyMetadataPackageSections Sections { get; init; } = new();

    [JsonPropertyName("categories")]
    public List<FamilyMetadataCategoryNode> Categories { get; init; } = [];

    [JsonPropertyName("attributes")]
    public List<FamilyMetadataAttribute> Attributes { get; init; } = [];

    [JsonPropertyName("bindings")]
    public List<FamilyMetadataBinding> Bindings { get; init; } = [];
}

public sealed class FamilyMetadataPackageSections
{
    [JsonPropertyName("categories")]
    public bool Categories { get; init; }

    [JsonPropertyName("attributes")]
    public bool Attributes { get; init; }

    [JsonPropertyName("bindings")]
    public bool Bindings { get; init; }
}

public sealed class FamilyMetadataCategoryNode
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("children")]
    public List<FamilyMetadataCategoryNode> Children { get; init; } = [];
}

public sealed class FamilyMetadataAttribute
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("group")]
    public string? Group { get; init; }
}

public sealed class FamilyMetadataBinding
{
    [JsonPropertyName("categoryPath")]
    public string CategoryPath { get; init; } = string.Empty;

    [JsonPropertyName("attributeName")]
    public string AttributeName { get; init; } = string.Empty;

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; init; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; init; } = true;
}

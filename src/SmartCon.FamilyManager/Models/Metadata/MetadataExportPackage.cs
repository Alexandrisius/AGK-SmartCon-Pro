using System.Text.Json.Serialization;

namespace SmartCon.FamilyManager.Models.Metadata;

public sealed class MetadataExportPackage
{
    [JsonPropertyName("format")]
    public string Format { get; init; } = "smartcon.familymanager.metadata-package";

    [JsonPropertyName("version")]
    public int Version { get; init; } = 2;

    [JsonPropertyName("exportedAtUtc")]
    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("sections")]
    public MetadataExportSections Sections { get; init; } = new();

    [JsonPropertyName("categories")]
    public List<MetadataExportCategoryNode> Categories { get; init; } = [];

    [JsonPropertyName("attributes")]
    public List<MetadataExportAttribute> Attributes { get; init; } = [];

    [JsonPropertyName("bindings")]
    public List<MetadataExportBinding> Bindings { get; init; } = [];
}

public sealed class MetadataExportSections
{
    [JsonPropertyName("categories")]
    public bool Categories { get; init; }

    [JsonPropertyName("attributes")]
    public bool Attributes { get; init; }

    [JsonPropertyName("bindings")]
    public bool Bindings { get; init; }
}

public sealed class MetadataExportCategoryNode
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("children")]
    public List<MetadataExportCategoryNode> Children { get; init; } = [];
}

public sealed class MetadataExportAttribute
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("group")]
    public string? Group { get; init; }
}

public sealed class MetadataExportBinding
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

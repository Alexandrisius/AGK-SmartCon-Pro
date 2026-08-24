using System.Text.Json.Serialization;

namespace SmartCon.FamilyManager.Models.Metadata;

public sealed class MetadataExportPackage
{
    [JsonPropertyName("format")]
    public string Format { get; init; } = "smartcon.familymanager.metadata-package";

    [JsonPropertyName("version")]
    public int Version { get; init; } = 4;

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

    /// <summary>
    /// Auto-assignment rules (#241, package v4). Empty for v1-v3 packages.
    /// Old plugin versions ignore this section (System.Text.Json skips
    /// unknown properties) — forward and backward compatible.
    /// </summary>
    [JsonPropertyName("assignmentRules")]
    public List<MetadataExportAssignmentRule> AssignmentRules { get; init; } = [];
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

    /// <summary>
    /// Import Validation Gate (package v3): validation rules attached to
    /// this binding. Empty for v2 packages — they import rule-free.
    /// Unknown rule operators are skipped on import (forward-compat).
    /// </summary>
    [JsonPropertyName("validationRules")]
    public List<MetadataExportValidationRule> ValidationRules { get; init; } = [];
}

public sealed class MetadataExportValidationRule
{
    [JsonPropertyName("operator")]
    public string Operator { get; init; } = string.Empty;

    [JsonPropertyName("valueText")]
    public string? ValueText { get; init; }

    [JsonPropertyName("valueNumber")]
    public double? ValueNumber { get; init; }

    [JsonPropertyName("minValue")]
    public double? MinValue { get; init; }

    [JsonPropertyName("maxValue")]
    public double? MaxValue { get; init; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// Auto-assignment rules of one category (#241, package v4). The category
/// is addressed by its full path (same as bindings); attributes inside
/// conditions by name.
/// </summary>
public sealed class MetadataExportAssignmentRule
{
    [JsonPropertyName("categoryPath")]
    public string CategoryPath { get; init; } = string.Empty;

    [JsonPropertyName("groups")]
    public List<MetadataExportAssignmentGroup> Groups { get; init; } = [];
}

public sealed class MetadataExportAssignmentGroup
{
    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; init; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; init; } = true;

    [JsonPropertyName("conditions")]
    public List<MetadataExportAssignmentCondition> Conditions { get; init; } = [];
}

public sealed class MetadataExportAssignmentCondition
{
    /// <summary>"attribute" or "system" (enum name of
    /// <c>AssignmentConditionSourceKind</c>).</summary>
    [JsonPropertyName("sourceKind")]
    public string SourceKind { get; init; } = string.Empty;

    /// <summary>Library attribute name for sourceKind = "attribute".</summary>
    [JsonPropertyName("attributeName")]
    public string? AttributeName { get; init; }

    /// <summary>System field enum name for sourceKind = "system"
    /// (RevitCategory / PartType / FamilyName / SystemFamilyKey).</summary>
    [JsonPropertyName("systemKey")]
    public string? SystemKey { get; init; }

    [JsonPropertyName("operator")]
    public string Operator { get; init; } = string.Empty;

    [JsonPropertyName("valueText")]
    public string? ValueText { get; init; }

    [JsonPropertyName("valueNumber")]
    public double? ValueNumber { get; init; }

    [JsonPropertyName("minValue")]
    public double? MinValue { get; init; }

    [JsonPropertyName("maxValue")]
    public double? MaxValue { get; init; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; init; } = true;
}

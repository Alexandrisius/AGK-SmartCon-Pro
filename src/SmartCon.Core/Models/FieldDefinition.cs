namespace SmartCon.Core.Models;

public sealed record FieldDefinition
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public ValidationMode ValidationMode { get; init; } = ValidationMode.None;
    public List<string> AllowedValues { get; init; } = [];
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
}

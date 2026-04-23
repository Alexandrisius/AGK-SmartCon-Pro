namespace SmartCon.Core.Models;

public sealed record FileBlockDefinition
{
    public int Index { get; init; }
    public string Role { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;

    public static readonly string[] PredefinedRoles =
    [
        "project", "originator", "volume", "level", "type",
        "discipline", "number", "status", "milestone", "suitability", "revision", "custom"
    ];
}

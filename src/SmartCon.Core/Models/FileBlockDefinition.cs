namespace SmartCon.Core.Models;

public sealed record FileBlockDefinition
{
    public int Index { get; init; }
    public string Field { get; init; } = string.Empty;
    public ParseRule ParseRule { get; init; } = new();
}

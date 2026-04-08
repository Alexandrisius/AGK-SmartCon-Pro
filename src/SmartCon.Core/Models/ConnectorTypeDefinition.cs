namespace SmartCon.Core.Models;

public sealed record ConnectorTypeDefinition
{
    public required int Code { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    public override string ToString() => string.IsNullOrEmpty(Name) ? $"Code {Code}" : Name;
}

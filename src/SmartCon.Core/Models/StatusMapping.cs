namespace SmartCon.Core.Models;

public sealed record StatusMapping
{
    public string WipValue { get; init; } = string.Empty;
    public string SharedValue { get; init; } = string.Empty;
}

namespace SmartCon.Core.Models.Agent;

public sealed class ElementSummaryGroup
{
    public required string Key { get; init; }

    public int Count { get; init; }

    public double? Sum { get; init; }

    public string? SumUnit { get; init; }
}
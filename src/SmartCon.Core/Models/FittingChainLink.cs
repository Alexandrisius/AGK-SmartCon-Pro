namespace SmartCon.Core.Models;

public sealed record FittingChainLink
{
    public required FittingChainNodeType Type { get; init; }
    public required FittingMappingRule Rule { get; init; }
    public required FittingMapping Family { get; init; }
    public required ConnectionTypeCode CtcIn { get; init; }
    public required ConnectionTypeCode CtcOut { get; init; }
    public required double RadiusIn { get; init; }
    public required double RadiusOut { get; init; }
}

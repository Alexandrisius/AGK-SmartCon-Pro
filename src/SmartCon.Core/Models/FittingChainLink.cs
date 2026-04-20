namespace SmartCon.Core.Models;

/// <summary>
/// A single link in a fitting chain plan — either a fitting or a reducer.
/// Contains the mapping rule, resolved family, and connector type/radius on each side.
/// </summary>
public sealed record FittingChainLink
{
    /// <summary>Whether this link is a fitting or a reducer.</summary>
    public required FittingChainNodeType Type { get; init; }

    /// <summary>Mapping rule that produced this link.</summary>
    public required FittingMappingRule Rule { get; init; }

    /// <summary>Resolved fitting family and symbol.</summary>
    public required FittingMapping Family { get; init; }

    /// <summary>Connection type code on the "in" side (toward static).</summary>
    public required ConnectionTypeCode CtcIn { get; init; }

    /// <summary>Connection type code on the "out" side (toward dynamic).</summary>
    public required ConnectionTypeCode CtcOut { get; init; }

    /// <summary>Radius on the "in" side (internal units).</summary>
    public required double RadiusIn { get; init; }

    /// <summary>Radius on the "out" side (internal units).</summary>
    public required double RadiusOut { get; init; }
}

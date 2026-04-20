namespace SmartCon.Core.Models;

/// <summary>
/// Extended mapping rule: pair of connector types -> list of fitting families.
/// Stored in JSON (AppData). Loaded via IFittingMappingRepository.
/// </summary>
public sealed record FittingMappingRule
{
    /// <summary>Source connection type (from static side).</summary>
    public required ConnectionTypeCode FromType { get; init; }

    /// <summary>Target connection type (to dynamic side).</summary>
    public required ConnectionTypeCode ToType { get; init; }

    /// <summary>When true, connectors of these two types can be linked directly without a fitting.</summary>
    public bool IsDirectConnect { get; init; }

    /// <summary>Fitting families for this type transition (ordered by priority).</summary>
    public IReadOnlyList<FittingMapping> FittingFamilies { get; init; } = [];

    /// <summary>Reducer families for DN transition within the same type pair.</summary>
    public IReadOnlyList<FittingMapping> ReducerFamilies { get; init; } = [];
}

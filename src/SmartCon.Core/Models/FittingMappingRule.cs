namespace SmartCon.Core.Models;

/// <summary>
/// Extended mapping rule: pair of connector types -> list of fitting families.
/// Stored in JSON (AppData). Loaded via IFittingMappingRepository.
/// </summary>
public sealed record FittingMappingRule
{
    public required ConnectionTypeCode FromType { get; init; }
    public required ConnectionTypeCode ToType { get; init; }
    public bool IsDirectConnect { get; init; }
    public List<FittingMapping> FittingFamilies { get; init; } = [];

    /// <summary>
    /// Reducer fitting families for the case when FromType == ToType,
    /// but connector radii differ. If empty, the system will try to use
    /// families from FittingFamilies as reducers.
    /// </summary>
    public List<FittingMapping> ReducerFamilies { get; init; } = [];
}

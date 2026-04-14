namespace SmartCon.Core.Models;

/// <summary>
/// Represents a single available size configuration for a dynamic MEP element.
/// Contains connector radii, lookup table query parameters, and display information.
/// Used by the PipeConnect size dropdown and auto-selection algorithm.
/// </summary>
public sealed record FamilySizeOption
{
    public required string DisplayName { get; init; }

    public required double Radius { get; init; }

    public required int TargetConnectorIndex { get; init; }

    public IReadOnlyDictionary<int, double> AllConnectorRadii { get; init; } = new Dictionary<int, double>();

    public IReadOnlyList<double> QueryParameterRadiiFt { get; init; } = [];

    public int UniqueParameterCount { get; init; } = 1;

    public int TargetColumnIndex { get; init; } = 1;

    public IReadOnlyList<IReadOnlyList<int>> QueryParamConnectorGroups { get; init; } = [];

    public string Source { get; init; } = "FamilySymbol";

    public bool IsAutoSelect { get; init; }

    public string? SymbolName { get; init; }

    public string? CurrentSymbolName { get; init; }

    public IReadOnlyDictionary<string, string> NonSizeParameterValues { get; init; } = new Dictionary<string, string>();

    public bool RequiresNonSizeParameterChange { get; init; }

    public IReadOnlyList<string> QueryParamNames { get; init; } = [];

    public IReadOnlyList<double> QueryParamRawValuesMm { get; init; } = [];

    public override string ToString() => DisplayName;
}

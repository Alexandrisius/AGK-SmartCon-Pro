namespace SmartCon.Core.Models;

/// <summary>
/// Represents a single available size configuration for a dynamic MEP element.
/// Contains connector radii, lookup table query parameters, and display information.
/// Used by the PipeConnect size dropdown and auto-selection algorithm.
/// </summary>
public sealed record FamilySizeOption
{
    /// <summary>Display name shown in the size dropdown (e.g. "DN 65 × DN 50").</summary>
    public required string DisplayName { get; init; }

    /// <summary>Radius of the target connector (internal units).</summary>
    public required double Radius { get; init; }

    /// <summary>Connector index used as the primary size reference.</summary>
    public required int TargetConnectorIndex { get; init; }

    /// <summary>Radii of all connectors keyed by connector index.</summary>
    public IReadOnlyDictionary<int, double> AllConnectorRadii { get; init; } = new Dictionary<int, double>();

    /// <summary>Query parameter radii for lookup table matching.</summary>
    public IReadOnlyList<double> QueryParameterRadiiFt { get; init; } = [];

    /// <summary>Number of unique query parameters.</summary>
    public int UniqueParameterCount { get; init; } = 1;

    /// <summary>Column index of the target connector in the lookup table.</summary>
    public int TargetColumnIndex { get; init; } = 1;

    /// <summary>Groups of connector indices sharing the same lookup table column.</summary>
    public IReadOnlyList<IReadOnlyList<int>> QueryParamConnectorGroups { get; init; } = [];

    /// <summary>Data source: "LookupTable", "FamilySymbol", or empty for auto-select.</summary>
    public string Source { get; init; } = "FamilySymbol";

    /// <summary>Whether this is the auto-select option (uses current element size).</summary>
    public bool IsAutoSelect { get; init; }

    /// <summary>FamilySymbol name for this size option.</summary>
    public string? SymbolName { get; init; }

    /// <summary>Currently active FamilySymbol name (for comparison).</summary>
    public string? CurrentSymbolName { get; init; }

    /// <summary>Non-size parameter values that differ from the current configuration.</summary>
    public IReadOnlyDictionary<string, string> NonSizeParameterValues { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether changing to this option requires non-size parameter updates.</summary>
    public bool RequiresNonSizeParameterChange { get; init; }

    /// <summary>Names of lookup table query parameters (DN columns).</summary>
    public IReadOnlyList<string> QueryParamNames { get; init; } = [];

    /// <summary>Raw query parameter values in millimeters.</summary>
    public IReadOnlyList<double> QueryParamRawValuesMm { get; init; } = [];

    public override string ToString() => DisplayName;
}

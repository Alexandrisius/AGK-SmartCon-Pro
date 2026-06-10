namespace SmartCon.Core.Models;

/// <summary>
/// One row from a Revit FamilySizeTable (lookup table) or FamilySymbol enumeration.
/// Contains connector radii, query parameters, and non-size parameter values for a single size configuration.
/// </summary>
public sealed record SizeTableRow
{
    /// <summary>Column index of the target connector in the lookup table.</summary>
    public required int TargetColumnIndex { get; init; }

    /// <summary>Radius of the target connector for this row (internal units).</summary>
    public required double TargetRadiusFt { get; init; }

    /// <summary>Radii of all connectors keyed by connector index.</summary>
    public required IReadOnlyDictionary<int, double> ConnectorRadiiFt { get; init; }

    /// <summary>Query parameter radii extracted from lookup table columns.</summary>
    public IReadOnlyList<double> QueryParameterRadiiFt { get; init; } = [];

    /// <summary>Number of unique query parameters (distinct DN columns).</summary>
    public int UniqueQueryParameterCount { get; init; } = 1;

    /// <summary>Groups of connector indices sharing the same query parameter column.</summary>
    public IReadOnlyList<IReadOnlyList<int>> QueryParamConnectorGroups { get; init; } = [];

    /// <summary>Non-size parameter values for this row (e.g. angle, material).</summary>
    public IReadOnlyDictionary<string, string> NonSizeParameterValues { get; init; } = new Dictionary<string, string>();

    /// <summary>Names of query parameters (DN columns).</summary>
    public IReadOnlyList<string> QueryParamNames { get; init; } = [];

    /// <summary>Raw query parameter values in millimeters.</summary>
    public IReadOnlyList<double> QueryParamRawValuesMm { get; init; } = [];
}

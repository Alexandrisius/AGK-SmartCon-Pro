namespace SmartCon.Core.Models;

/// <summary>
/// One row from a Revit FamilySizeTable (lookup table) or FamilySymbol enumeration.
/// Contains connector radii, query parameters, and non-size parameter values for a single size configuration.
/// </summary>
public sealed record SizeTableRow
{
    public required int TargetColumnIndex { get; init; }

    public required double TargetRadiusFt { get; init; }

    public required IReadOnlyDictionary<int, double> ConnectorRadiiFt { get; init; }

    public IReadOnlyList<double> QueryParameterRadiiFt { get; init; } = [];

    public int UniqueQueryParameterCount { get; init; } = 1;

    public IReadOnlyList<IReadOnlyList<int>> QueryParamConnectorGroups { get; init; } = [];

    public IReadOnlyDictionary<string, string> NonSizeParameterValues { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> QueryParamNames { get; init; } = [];

    public IReadOnlyList<double> QueryParamRawValuesMm { get; init; } = [];
}

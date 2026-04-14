namespace SmartCon.Core.Models;

/// <summary>
/// Constraint on a lookup table column value from another fitting connector.
/// Used when searching for available DN for multi-query size_lookup:
///   size_lookup(Table, target, default, DN1, DN2)
/// For DN1 connector constraints = [ LookupColumnConstraint(connIdx2, "DN2", 20.0) ]
/// -> show only rows where DN2 is approximately 20 mm.
/// </summary>
public sealed record LookupColumnConstraint(
    int ConnectorIndex,
    string ParameterName,
    double ValueMm
);

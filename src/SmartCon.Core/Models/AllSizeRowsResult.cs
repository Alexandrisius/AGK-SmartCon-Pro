namespace SmartCon.Core.Models;

/// <summary>
/// Aggregated result of reading all size rows from lookup tables and/or FamilySymbol enumeration.
/// </summary>
/// <param name="Rows">Flat list of all size rows across tables.</param>
/// <param name="AllNonSizeParamNames">Non-DN parameter names found across all rows.</param>
/// <param name="PerTableRows">Size rows grouped by individual lookup table.</param>
/// <param name="ValidDnKeys">DN column key values that passed validation.</param>
public sealed record AllSizeRowsResult(
    IReadOnlyList<SizeTableRow> Rows,
    IEnumerable<string> AllNonSizeParamNames,
    IReadOnlyList<IReadOnlyList<SizeTableRow>> PerTableRows,
    IEnumerable<long> ValidDnKeys);

namespace SmartCon.Core.Models;

/// <summary>
/// Information about a fitting family that passed PipeConnect eligibility criteria.
/// Used in IFittingFamilyRepository and FamilySelectorView.
/// </summary>
public sealed record FamilyInfo(
    string FamilyName,
    string? PartTypeName,
    int ConnectorCount,
    IReadOnlyList<string> SymbolNames
);

namespace SmartCon.Core.Models;

/// <summary>
/// Информация о семействе фитинга, прошедшем фильтрацию по критериям PipeConnect.
/// Используется в IFittingFamilyRepository и FamilySelectorView.
/// </summary>
public sealed record FamilyInfo(
    string FamilyName,
    string? PartTypeName,
    int ConnectorCount,
    IReadOnlyList<string> SymbolNames
);

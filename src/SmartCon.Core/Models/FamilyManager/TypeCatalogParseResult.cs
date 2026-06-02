namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Результат парсинга Type Catalog (.txt) семейства Revit.
/// </summary>
public sealed record TypeCatalogParseResult(
    IReadOnlyList<string> ParameterNames,
    IReadOnlyList<TypeCatalogEntry> Entries)
{
    public bool HasEntries => Entries.Count > 0;
}

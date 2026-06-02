namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Одна запись (тип) из Type Catalog (.txt) семейства Revit.
/// </summary>
public sealed record TypeCatalogEntry(
    string TypeName,
    IReadOnlyDictionary<string, string> ParameterValues);

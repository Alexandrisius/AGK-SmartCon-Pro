namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Parameters for a stale update operation (single or batch).
/// </summary>
/// <param name="CatalogItemIds">Catalog items to update.</param>
/// <param name="OverwriteParameterValues">
/// If true, parameter values in the project are overwritten with values from the new family.
/// If false, parameter values are preserved (Revit's <c>FamilyLoadOptions.OverwriteParameterValues = false</c>).
/// </param>
/// <param name="Recursive">Reserved for future use. Currently always true.</param>
public sealed record StaleUpdateRequest(
    IReadOnlyList<string> CatalogItemIds,
    bool OverwriteParameterValues,
    bool Recursive = true);

namespace SmartCon.Core.Models.FamilyManager;

public record SystemFamilyImportResult(
    bool Success,
    string? Message,
    string? CatalogItemId,
    int TypesCount);

namespace SmartCon.Core.Models.FamilyManager;

public sealed record FamilyTypeDescriptor(
    string Id,
    string CatalogItemId,
    string Name,
    int SortOrder);

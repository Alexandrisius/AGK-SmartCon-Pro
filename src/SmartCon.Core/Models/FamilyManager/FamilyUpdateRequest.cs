namespace SmartCon.Core.Models.FamilyManager;

public sealed record FamilyUpdateRequest(
    string CatalogItemId,
    string FilePath,
    int RevitMajorVersion);

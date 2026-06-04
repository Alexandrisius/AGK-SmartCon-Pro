namespace SmartCon.Core.Models.FamilyManager;

public sealed record SystemFamilyBatchImportItem(
    string Id,
    string SourceCategoryName,
    string FamilyName,
    string NormalizedName,
    IReadOnlyList<string> TypeNames,
    int TypeCount,
    FamilyBatchImportStatus Status,
    FamilyBatchImportAction Action,
    string? ExistingCatalogItemId = null,
    string? TempRvtPath = null,
    string? Sha256 = null,
    string? TargetCategoryId = null);

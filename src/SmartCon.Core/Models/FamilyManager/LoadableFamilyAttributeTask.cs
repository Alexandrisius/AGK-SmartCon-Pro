namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Task for <see cref="IFamilyDataExtractionService.Extract"/> after successful
/// loadable-family import into managed storage.
/// </summary>
public sealed record LoadableFamilyAttributeTask(
    string CatalogItemId,
    string ManagedRfaPath,
    string? VersionId,
    string? FileId,
    bool HasTypeCatalog);

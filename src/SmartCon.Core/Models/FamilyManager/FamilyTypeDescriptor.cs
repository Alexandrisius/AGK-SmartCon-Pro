namespace SmartCon.Core.Models.FamilyManager;

public sealed record FamilyTypeDescriptor(
    string Id,
    string CatalogItemId,
    string Name,
    int SortOrder,
    string? VersionId = null,
    string? FileId = null,
    string? ExtractionRunId = null,
    string? UniqueId = null);

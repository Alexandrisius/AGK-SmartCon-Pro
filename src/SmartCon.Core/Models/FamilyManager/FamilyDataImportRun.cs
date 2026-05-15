namespace SmartCon.Core.Models.FamilyManager;

public sealed record FamilyDataImportRun(
    string Id,
    string CatalogItemId,
    string? VersionId,
    string? FileId,
    string? SourceSha256,
    int RevitMajorVersion,
    FamilyDataImportStatus Status,
    int TypesCount,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ErrorMessage);

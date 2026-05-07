namespace SmartCon.Core.Models.FamilyManager;

public sealed record ExtractedAttributeValue(
    string Id,
    string CatalogItemId,
    string? VersionId,
    string? FileId,
    string? TypeId,
    string AttributeId,
    string? BindingId,
    string ParameterName,
    AttributeScope? ParameterScope,
    string? StorageType,
    string? ValueText,
    string? ValueRaw,
    double? ValueNumber,
    string? UnitTypeId,
    AttributeValueStatus Status,
    string? Message,
    string ExtractionRunId,
    DateTimeOffset ExtractedAtUtc);

namespace SmartCon.Core.Services.Storage.Dto;

/// <summary>
/// Root DTO for persisted fitting mapping (ExtensibleStorage payload and import/export JSON file).
/// Designed for forward compatibility: unknown future fields are ignored by
/// <see cref="System.Text.Json.JsonSerializer"/> with default options, and
/// <see cref="SchemaVersion"/> defaults to <c>0</c> for legacy files that omit it.
/// </summary>
internal sealed class MappingPayloadDto
{
    /// <summary>
    /// Format version of the payload. <c>0</c> indicates a legacy file without the field —
    /// <see cref="FittingMappingJsonSerializer"/> normalizes such values to
    /// <see cref="FittingMappingJsonSerializer.CurrentVersion"/>.
    /// </summary>
    public int SchemaVersion { get; set; }

    public List<ConnectorTypeDto>? ConnectorTypes { get; set; }

    public List<MappingRuleDto>? MappingRules { get; set; }
}

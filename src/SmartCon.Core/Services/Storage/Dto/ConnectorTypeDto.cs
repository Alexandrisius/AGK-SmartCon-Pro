namespace SmartCon.Core.Services.Storage.Dto;

/// <summary>
/// Serialization DTO for <see cref="Models.ConnectorTypeDefinition"/>.
/// Property names match the legacy JSON format (camelCase via JsonSerializerOptions).
/// </summary>
internal sealed class ConnectorTypeDto
{
    public int Code { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }
}

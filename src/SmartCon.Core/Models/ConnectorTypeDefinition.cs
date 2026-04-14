namespace SmartCon.Core.Models;

/// <summary>
/// Defines a connector type (ConnectionTypeCode) with a numeric code, name, and description.
/// Stored in the fitting mapping JSON and written to the Revit family Description parameter.
/// Format: "{Code}.{Name}.{Description}".
/// </summary>
public sealed record ConnectorTypeDefinition
{
    public required int Code { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    public override string ToString() => string.IsNullOrEmpty(Name) ? $"Code {Code}" : Name;
}

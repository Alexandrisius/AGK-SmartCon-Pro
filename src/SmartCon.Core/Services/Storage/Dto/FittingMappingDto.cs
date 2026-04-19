namespace SmartCon.Core.Services.Storage.Dto;

/// <summary>
/// Serialization DTO for <see cref="Models.FittingMapping"/>.
/// </summary>
internal sealed class FittingMappingDto
{
    public string? FamilyName { get; set; }

    public string? SymbolName { get; set; }

    public int Priority { get; set; }
}

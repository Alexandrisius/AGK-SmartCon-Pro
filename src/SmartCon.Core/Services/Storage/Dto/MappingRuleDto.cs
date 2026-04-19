namespace SmartCon.Core.Services.Storage.Dto;

/// <summary>
/// Serialization DTO for <see cref="Models.FittingMappingRule"/>.
/// </summary>
internal sealed class MappingRuleDto
{
    public int FromType { get; set; }

    public int ToType { get; set; }

    public bool IsDirectConnect { get; set; }

    public List<FittingMappingDto>? FittingFamilies { get; set; }

    public List<FittingMappingDto>? ReducerFamilies { get; set; }
}

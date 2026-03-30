namespace SmartCon.Core.Models;

/// <summary>
/// Расширенное правило маппинга: пара типов коннекторов -> список семейств фитингов.
/// Хранится в JSON (AppData). Загружается через IFittingMappingRepository.
/// </summary>
public sealed record FittingMappingRule
{
    public required ConnectionTypeCode FromType { get; init; }
    public required ConnectionTypeCode ToType { get; init; }
    public bool IsDirectConnect { get; init; }
    public List<FittingMapping> FittingFamilies { get; init; } = [];
}

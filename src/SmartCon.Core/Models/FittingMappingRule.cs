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

    /// <summary>
    /// Семейства фитингов-переходников сечения для случая когда FromType == ToType,
    /// но радиусы коннекторов различаются. Если пуст — система попытается использовать
    /// фитинги из FittingFamilies как переходники.
    /// </summary>
    public List<FittingMapping> ReducerFamilies { get; init; } = [];
}

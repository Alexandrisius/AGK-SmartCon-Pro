namespace SmartCon.Core.Models;

/// <summary>
/// Модель одного семейства фитинга в правиле маппинга.
/// Хранится в JSON (AppData). Используется для подбора фитингов в S5.
/// </summary>
public sealed record FittingMapping
{
    public required string FamilyName { get; init; }
    public string SymbolName { get; init; } = "*";
    public int Priority { get; init; }
}

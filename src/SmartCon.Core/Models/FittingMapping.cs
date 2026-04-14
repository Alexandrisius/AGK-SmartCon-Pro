namespace SmartCon.Core.Models;

/// <summary>
/// Model of a single fitting family in a mapping rule.
/// Stored in JSON (AppData). Used for fitting selection in S5.
/// </summary>
public sealed record FittingMapping
{
    public required string FamilyName { get; init; }
    public string SymbolName { get; init; } = "*";
    public int Priority { get; init; }
}

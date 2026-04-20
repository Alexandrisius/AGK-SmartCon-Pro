namespace SmartCon.Core.Models;

/// <summary>
/// Model of a single fitting family in a mapping rule.
/// Stored in JSON (AppData). Used for fitting selection in S5.
/// </summary>
public sealed record FittingMapping
{
    /// <summary>Revit family name.</summary>
    public required string FamilyName { get; init; }

    /// <summary>Revit type/symbol name. "*" means any symbol.</summary>
    public string SymbolName { get; init; } = "*";

    /// <summary>Selection priority (lower = higher priority).</summary>
    public int Priority { get; init; }
}

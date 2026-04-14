namespace SmartCon.Core.Models;

/// <summary>
/// Available size for a dynamic family.
/// </summary>
public sealed record SizeOption
{
    /// <summary>Display name in ComboBox (e.g. "DN 25", "AUTO-SELECT (current)").</summary>
    public required string DisplayName { get; init; }

    /// <summary>Radius in Revit internal units (feet).</summary>
    public required double Radius { get; init; }

    /// <summary>Data source: "LookupTable", "FamilySymbol", "PipeType" or empty for auto-select.</summary>
    public string Source { get; init; } = "FamilySymbol";

    /// <summary>"AUTO-SELECT" flag — use current auto-selection logic.</summary>
    public bool IsAutoSelect { get; init; }

    public override string ToString() => DisplayName;
}

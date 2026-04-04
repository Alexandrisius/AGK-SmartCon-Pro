namespace SmartCon.Core.Models;

/// <summary>
/// Доступный размер для динамического семейства.
/// </summary>
public sealed record SizeOption
{
    /// <summary>Отображаемое имя в ComboBox (напр. "DN 25", "АВТОПОДБОР (текущий)").</summary>
    public required string DisplayName { get; init; }

    /// <summary>Радиус во внутренних единицах Revit (футы).</summary>
    public required double Radius { get; init; }

    /// <summary>Источник данных: "LookupTable", "FamilySymbol", "PipeType" или пустой для автоподбора.</summary>
    public string Source { get; init; } = "FamilySymbol";

    /// <summary>Флаг "АВТОПОДБОР" — использовать текущую логику подбора.</summary>
    public bool IsAutoSelect { get; init; }

    public override string ToString() => DisplayName;
}

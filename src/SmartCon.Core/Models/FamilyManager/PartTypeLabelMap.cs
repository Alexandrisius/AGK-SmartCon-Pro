using SmartCon.Core.Services;

namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Localized labels for Part Type values (ADR-055). Maps the enum ordinal
/// stored in <see cref="FamilyFact.ValueKey"/> to a RU/EN display string
/// following the current UI language — unlike
/// <see cref="FamilyFact.ValueDisplay"/>, which freezes the
/// extraction-time English member name, this map keeps the displayed value
/// consistent with the UI language. Unknown/newer ordinals (added by
/// Autodesk after this map was written) return <c>null</c> and the caller
/// falls back to <see cref="FamilyFact.ValueDisplay"/>.
/// Values verified against the Revit 2025 API PartType enumeration
/// (revitapidocs.com/2025, 4182a378-a3cc-7f0c-87b3-d230c2540267); the RU
/// strings mirror the official Autodesk Russian localization verbatim
/// (help.autodesk.com/cloudhelp/2023/RUS/Revit-Customize, tables
/// GUID-54F9DD0A and GUID-4DA88E95) — do NOT invent own translations.
/// </summary>
public static class PartTypeLabelMap
{
    private static readonly IReadOnlyDictionary<int, (string Ru, string En)> Labels =
        new Dictionary<int, (string Ru, string En)>
        {
            // RU — официальная локализация Autodesk (help.autodesk.com/cloudhelp/2023/RUS/
            // Revit-Customize, GUID-54F9DD0A + GUID-4DA88E95). Не выдумывать свои варианты.
            [-1] = ("Не определён", "Undefined"),
            [0] = ("Нормальный", "Normal"),
            [1] = ("Монтаж на воздуховод", "Duct Mounted"),
            [2] = ("Распределительная коробка", "Junction Box"),
            [3] = ("Присоединяется", "Attaches To"),
            [4] = ("Вставляется", "Breaks Into"),
            [5] = ("Отвод", "Elbow"),
            [6] = ("Тройник", "Tee"),
            [7] = ("Переход", "Transition"),
            [8] = ("Крестовина", "Cross"),
            [9] = ("Заглушка", "Cap"),
            [10] = ("Врезка - По нормали", "Tap - Perpendicular"),
            [11] = ("Врезка - Регулируемая", "Tap - Adjustable"),
            [12] = ("Смещение", "Offset"),
            [13] = ("Соединение", "Union"),
            [14] = ("Распределительный щит", "Panel Board"),
            [15] = ("Трансформатор", "Transformer"),
            [16] = ("Коммутационный щит", "Switchboard"),
            [17] = ("Другой щит", "Other Panel"),
            [18] = ("Выключатель оборудования", "Equipment Switch"),
            [19] = ("Выключатель", "Switch"),
            [20] = ("Клапан - Вставляется", "Valve - Breaks Into"),
            [21] = ("Соединительный патрубок - По нормали", "Spud - Perpendicular"),
            [22] = ("Соединительный патрубок - Регулируемый", "Spud - Adjustable"),
            [23] = ("Заслонка", "Damper"),
            [24] = ("Косой тройник", "Wye"),
            [25] = ("Боковой тройник", "Lateral Tee"),
            [26] = ("Боковая крестовина", "Lateral Cross"),
            [27] = ("Штанообразный тройник", "Pants"),
            [28] = ("Мультипорт", "Multi-Port"),
            [29] = ("Клапан по нормали", "Valve - Normal"),
            [30] = ("Тройник распределительной коробки", "Junction Box - Tee"),
            [31] = ("Крестовина распределительной коробки", "Junction Box - Cross"),
            [32] = ("Фланец", "Pipe Flange"),
            [34] = ("Отвод распределительной коробки", "Junction Box - Elbow"),
            [35] = ("Отвод канального", "Channel Cable Tray - Elbow"),
            [36] = ("Вертикальный отвод канального", "Channel Cable Tray - Vertical Elbow"),
            [37] = ("Крестовина канального", "Channel Cable Tray - Cross"),
            [38] = ("Тройник канального", "Channel Cable Tray - Tee"),
            [39] = ("Переход канального", "Channel Cable Tray - Transition"),
            [40] = ("Муфта канального", "Channel Cable Tray - Union"),
            [41] = ("Угловой отвод", "Channel Cable Tray - Offset"),
            [42] = ("Мультипорт канального", "Channel Cable Tray - Multi-Port"),
            [43] = ("Отвод ступенчатого", "Ladder Cable Tray - Elbow"),
            [44] = ("Вертикальный отвод ступенчатого", "Ladder Cable Tray - Vertical Elbow"),
            [45] = ("Крестовина ступенчатого", "Ladder Cable Tray - Cross"),
            [46] = ("Тройник ступенчатого", "Ladder Cable Tray - Tee"),
            [47] = ("Переход ступенчатого", "Ladder Cable Tray - Transition"),
            [48] = ("Муфта ступенчатого", "Ladder Cable Tray - Union"),
            [49] = ("Угловой отвод ступенчатого", "Ladder Cable Tray - Offset"),
            [50] = ("Мультипорт ступенчатого", "Ladder Cable Tray - Multi-Port"),
            [51] = ("Встроенный датчик", "Inline Sensor"),
            [52] = ("Датчик", "Sensor"),
            [53] = ("Торцовая крышка", "End Cap"),
            [54] = ("Фурнитура кронштейна поручня", "Handrail Bracket Hardware"),
            [55] = ("Фурнитура кронштейна панели", "Panel Bracket Hardware"),
            [56] = ("Концевая фурнитура", "Termination Hardware"),
            [57] = ("Рельсы", "Rails"),
            [58] = ("Поручни", "Handrails"),
            [59] = ("Верхние рельсы", "Top Rails"),
            [60] = ("Механическое сочленение", "Pipe Mechanical Coupling"),
        };

    /// <summary>
    /// The localized label for a stored <see cref="FamilyFact.ValueKey"/>,
    /// or <c>null</c> when the key is not a known Part Type ordinal
    /// (caller falls back to <see cref="FamilyFact.ValueDisplay"/>).
    /// </summary>
    public static string? TryGetLabel(string valueKey)
    {
        if (!int.TryParse(valueKey, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var ordinal))
            return null;
        return TryGetLabel(ordinal);
    }

    /// <summary>
    /// The localized label for a Part Type ordinal, or <c>null</c> when the
    /// ordinal is not in the map.
    /// </summary>
    public static string? TryGetLabel(int ordinal)
    {
        if (!Labels.TryGetValue(ordinal, out var pair))
            return null;
        return LocalizationService.CurrentLanguage == Language.RU ? pair.Ru : pair.En;
    }

    /// <summary>
    /// All known Part Type entries as (ordinal-string key, localized
    /// label), sorted by label — the picker source for the auto-assignment
    /// editor (#241). Keys are the invariant ordinal strings stored in
    /// <see cref="FamilyFact.ValueKey"/> and assignment condition values.
    /// </summary>
    public static IReadOnlyList<(string Key, string Label)> GetAllEntries()
    {
        var currentLanguage = LocalizationService.CurrentLanguage;
        return Labels
            .Select(kvp => (Key: kvp.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Label: currentLanguage == Language.RU ? kvp.Value.Ru : kvp.Value.En))
            .OrderBy(e => e.Label, StringComparer.CurrentCulture)
            .ToList();
    }
}

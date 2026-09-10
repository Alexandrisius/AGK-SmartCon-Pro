using SmartCon.Core.Services;

namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Localized labels for the <c>connector_shape</c> fact (ADR-055, owner
/// stress test 2026-09-01): maps the connector-profile bitmask stored in
/// <see cref="FamilyFact.ValueKey"/> (Round=1, Rectangular=2, Oval=4) to a
/// RU/EN display string following the current UI language. A multi-shape
/// transition (round-to-rect) reads «Круглый и Прямоугольный» — never the
/// extraction-time «Round+Rectangular» fallback of
/// <see cref="FamilyFact.ValueDisplay"/>. Unknown masks return <c>null</c>
/// and the caller falls back to <see cref="FamilyFact.ValueDisplay"/>
/// (same contract as <see cref="PartTypeLabelMap"/>).
/// </summary>
public static class ConnectorShapeLabelMap
{
    private static readonly (int Bit, string Ru, string En)[] Shapes =
    [
        (RoutingGroupCatalog.ShapeRound, "Круглый", "Round"),
        (RoutingGroupCatalog.ShapeRectangular, "Прямоугольный", "Rectangular"),
        (RoutingGroupCatalog.ShapeOval, "Овальный", "Oval"),
    ];

    /// <summary>
    /// Localized label of a genuinely connectorless family (mask 0 — the
    /// fact WAS evaluated and found no connectors; owner stress test
    /// 2026-09-01: an empty value in the header looked like a bug).
    /// </summary>
    public static string NoConnectorsLabel
        => LocalizationService.CurrentLanguage == Language.RU ? "Нет коннекторов" : "No connectors";

    /// <summary>
    /// <c>true</c> when the stored key is the evaluated zero mask (a family
    /// without connectors) — distinct from the empty sentinel (fact absent)
    /// and from unparsable legacy values.
    /// </summary>
    public static bool IsZeroMask(string? valueKey)
        => int.TryParse(valueKey, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var mask) && mask == 0;

    /// <summary>
    /// The localized label for a stored <see cref="FamilyFact.ValueKey"/>
    /// bitmask, or <c>null</c> when the key is not a known mask (0, empty,
    /// unparsable — caller falls back to <see cref="FamilyFact.ValueDisplay"/>).
    /// </summary>
    public static string? TryGetLabel(string? valueKey)
    {
        if (!int.TryParse(valueKey, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var mask)
            || mask <= 0)
        {
            return null;
        }

        var russian = LocalizationService.CurrentLanguage == Language.RU;
        var names = new List<string>(3);
        foreach (var (bit, ru, en) in Shapes)
        {
            if ((mask & bit) != 0)
            {
                names.Add(russian ? ru : en);
            }
        }
        if (names.Count == 0)
        {
            return null;
        }

        var and = russian ? " и " : " and ";
        return names.Count == 1
            ? names[0]
            : string.Join(", ", names.GetRange(0, names.Count - 1)) + and + names[names.Count - 1];
    }
}

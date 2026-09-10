namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One extraction/display rule of the family-facts subsystem (ADR-055):
/// "for families whose Revit category is <see cref="CategoryId"/>, extract
/// the built-in parameter <see cref="ParameterId"/> and store it under
/// <see cref="FactKey"/>; display it with the localization key
/// <see cref="LabelKey"/>". Rules are evaluated in three places from this
/// single registry — extraction (SmartCon.Revit), migration detection
/// (the <c>family-facts-v1</c> actualization task builds its SQL from the
/// registered category ids and fact keys) and the properties window
/// (label lookup by category). Adding a new category-driven attribute is
/// a one-line change here — no DDL, no new task, no UI edits.
/// </summary>
/// <param name="CategoryId"><c>BuiltInCategory</c> ordinal of the target
/// family category (e.g. -2008049 = OST_PipeFitting). Stored as a raw int
/// so the value matches <c>catalog_items.revit_category_id</c> in SQL.
/// Deliberately NOT the enum: Core is loaded by unit tests where the
/// RevitAPI assembly is runtime-excluded (Nice3point
/// ExcludeAssets=runtime) — any enum-typed member here would throw
/// FileNotFoundException on first touch (I-09).</param>
/// <param name="FactKey">Stable machine key stored in
/// <c>family_facts.fact_key</c> (e.g. <c>"part_type"</c>).</param>
/// <param name="LabelKey">Localization key for the row label
/// (e.g. <c>"FM_Fact_PartType"</c>).</param>
/// <param name="ParameterId"><c>BuiltInParameter</c> ordinal to read from
/// the <c>Family</c> element of the family document (e.g. -1114206 =
/// FAMILY_CONTENT_PART_TYPE). The Revit-side extractor casts it back to
/// the enum — raw int here for the same I-09 reason as
/// <paramref name="CategoryId"/>.</param>
public sealed record FamilyFactRule(
    int CategoryId,
    string FactKey,
    string LabelKey,
    int ParameterId);

/// <summary>
/// Static registry of <see cref="FamilyFactRule"/> entries (ADR-055).
/// Current scope: the Part Type fact for the four MEP fitting categories
/// (pipe/duct/cable tray/conduit fittings) — accessories are deliberately
/// excluded (product decision: Part Type is surfaced for fittings only).
/// Category ordinals verified against the BuiltInCategory enumeration
/// (revitapidocs.com 2025/2026): OST_PipeFitting=-2008049,
/// OST_DuctFitting=-2008010, OST_CableTrayFitting=-2008126,
/// OST_ConduitFitting=-2008128; FAMILY_CONTENT_PART_TYPE=-1114206.
/// </summary>
public static class FamilyFactRuleSet
{
    /// <summary>Machine key of the Part Type fact
    /// (FAMILY_CONTENT_PART_TYPE).</summary>
    public const string PartTypeFactKey = "part_type";

    /// <summary>Localization key of the Part Type row label.</summary>
    public const string PartTypeLabelKey = "FM_Fact_PartType";

    /// <summary>Machine key of the connector-shape bitmask fact (owner
    /// stress test 2026-09-01, баг 8): the routing part picker filters flex
    /// duct candidates by the host's connector profile — a round flex duct
    /// must never offer rectangular-only fittings, while multi-shape
    /// transitions (oval-round, round-rect) stay offered because either end
    /// matches. The value is a bitmask: Round=1, Rectangular=2, Oval=4.</summary>
    public const string ConnectorShapeFactKey = "connector_shape";

    /// <summary>Localization key of the connector-shape row label.</summary>
    public const string ConnectorShapeLabelKey = "FM_Fact_ConnectorShape";

    private const int PartTypeParameterId = -1114206; // BuiltInParameter.FAMILY_CONTENT_PART_TYPE

    /// <summary><see cref="FamilyFactRule.ParameterId"/> of a COMPUTED fact
    /// (no backing built-in parameter — the extractor derives the value
    /// from the family geometry, e.g. the connector shapes).</summary>
    public const int ComputedFactParameterId = 0;

    /// <summary>All registered rules. Read-only by construction.</summary>
    public static IReadOnlyList<FamilyFactRule> Rules { get; } = new[]
    {
        PartTypeRule(-2008049), // OST_PipeFitting
        PartTypeRule(-2008010), // OST_DuctFitting
        PartTypeRule(-2008126), // OST_CableTrayFitting
        PartTypeRule(-2008128), // OST_ConduitFitting
        ConnectorShapeRule(-2008049), // OST_PipeFitting
        ConnectorShapeRule(-2008010), // OST_DuctFitting
        ConnectorShapeRule(-2008126), // OST_CableTrayFitting
        ConnectorShapeRule(-2008128), // OST_ConduitFitting
    };

    /// <summary>
    /// Distinct category ordinals covered by at least one rule — used by
    /// the actualization task's detection SQL and by UI label lookup.
    /// </summary>
    public static IReadOnlyCollection<int> CategoryIdsWithRules { get; } =
        Rules.Select(r => r.CategoryId).Distinct().ToArray();

    /// <summary>All rules registered for <paramref name="categoryId"/>
    /// (empty when the category has no facts).</summary>
    public static IReadOnlyList<FamilyFactRule> GetRulesForCategory(int categoryId) =>
        Rules.Where(r => r.CategoryId == categoryId).ToArray();

    /// <summary>The rule for (<paramref name="categoryId"/>,
    /// <paramref name="factKey"/>), or <c>null</c> when none is
    /// registered.</summary>
    public static FamilyFactRule? FindRule(int categoryId, string factKey) =>
        Rules.FirstOrDefault(r =>
            r.CategoryId == categoryId &&
            string.Equals(r.FactKey, factKey, StringComparison.Ordinal));

    private static FamilyFactRule PartTypeRule(int categoryId) => new(
        CategoryId: categoryId,
        FactKey: PartTypeFactKey,
        LabelKey: PartTypeLabelKey,
        ParameterId: PartTypeParameterId);

    private static FamilyFactRule ConnectorShapeRule(int categoryId) => new(
        CategoryId: categoryId,
        FactKey: ConnectorShapeFactKey,
        LabelKey: ConnectorShapeLabelKey,
        ParameterId: ComputedFactParameterId);
}


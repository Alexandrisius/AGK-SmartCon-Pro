namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Locale-invariant identity tokens for Revit system families (Issue #190,
/// ADR-064). <c>ElementType.FamilyName</c> is a LOCALIZED string
/// ("Conduit with Fittings" vs «Короб с фитингами») and cannot be the
/// sync identity on mixed-locale teams sharing one catalog database.
/// The family key is computed at extraction time from a per-category
/// discriminator API (see <c>SystemFamilyKeyResolver</c> in SmartCon.Revit)
/// and persisted into <c>family_types.family_key</c> (schema V27).
/// Keys are compared only within one category (the category ordinal is
/// part of the identity — see the presence snapshot in
/// <c>FamilyManagerMainViewModel.Tree</c>); multi-family tokens are
/// category-prefixed so they stay self-describing in logs.
/// </summary>
public static class SystemFamilyKeys
{
    /// <summary>Categories with exactly one system family (pipes, ducts,
    /// floors, roofs, ceilings, railings, insulations, wires, flex curves)
    /// — the key carries no discrimination and exists so the pipeline can
    /// treat every category uniformly.</summary>
    public const string SingleFamily = "Single";

    public const string ConduitWithFittings = "Conduit.WithFittings";
    public const string ConduitWithoutFittings = "Conduit.WithoutFittings";
    public const string CableTrayWithFittings = "CableTray.WithFittings";
    public const string CableTrayWithoutFittings = "CableTray.WithoutFittings";

    public const string WallBasic = "Wall.Basic";
    public const string WallCurtain = "Wall.Curtain";
    public const string WallStacked = "Wall.Stacked";
    public const string WallUnknown = "Wall.Unknown";

    public const string StairsAssembled = "Stairs.Assembled";
    public const string StairsCastInPlace = "Stairs.CastInPlace";
    public const string StairsPrecast = "Stairs.Precast";
    /// <summary>Unknown/future <c>StairsConstructionMethod</c> — mirrors
    /// <see cref="WallUnknown"/>; never silently degrades to
    /// <see cref="SingleFamily"/> so the fallback is self-describing.</summary>
    public const string StairsUnknown = "Stairs.Unknown";

    /// <summary>
    /// Duct shape discriminator (#215, FHV7): the "Воздуховоды" category has
    /// THREE system families (круглого / прямоугольного / овального сечения),
    /// resolved from <c>MEPCurveType.Shape</c> (<c>ConnectorProfileType</c>).
    /// Treating ducts as <see cref="SingleFamily"/> let a rectangular
    /// template prototype key-match a round reference — the sync created a
    /// type of the WRONG shape and the API assigned it incompatible
    /// (invisible-in-UI) fittings.
    /// </summary>
    public const string DuctRound = "Duct.Round";
    public const string DuctRectangular = "Duct.Rectangular";
    public const string DuctOval = "Duct.Oval";
    /// <summary>Unknown/future <c>ConnectorProfileType</c> (incl. Invalid) —
    /// mirrors <see cref="StairsUnknown"/>.</summary>
    public const string DuctUnknown = "Duct.Unknown";

    /// <summary>
    /// OST_FlexDuctCurves has TWO system families (круглого / прямоугольного
    /// сечения), resolved from <c>MEPCurveType.Shape</c>. Same collision class
    /// as #215 ducts (owner stress test 2026-08-30): with
    /// <see cref="SingleFamily"/> the staging prototype search took the first
    /// template flex-duct type — a rectangular one — and duplicated the round
    /// reference type into the WRONG family; the mini-project then carried a
    /// different Имя семейства and every unchanged reimport produced a
    /// phantom version.
    /// </summary>
    public const string FlexDuctRound = "FlexDuct.Round";
    public const string FlexDuctRectangular = "FlexDuct.Rectangular";
    /// <summary>Unknown/future <c>ConnectorProfileType</c> for flex ducts.</summary>
    public const string FlexDuctUnknown = "FlexDuct.Unknown";
}

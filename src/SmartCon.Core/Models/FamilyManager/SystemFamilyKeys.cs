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
}

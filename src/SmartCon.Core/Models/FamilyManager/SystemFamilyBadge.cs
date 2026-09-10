namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Issue #203: display badge distinguishing system family kinds on the
/// catalog tree type nodes — same-named types of different system families
/// (e.g. two types named «Короб» from «Короб с соединительными деталями»
/// and «Короб без соединительных деталей») were visually identical.
/// The badge encodes the discriminator already persisted in
/// <c>family_types.family_key</c> (ADR-064): duct/flex-duct profile shape,
/// fittings presence for conduits/cable trays, <c>WallType.Kind</c>,
/// <c>StairsType.ConstructionMethod</c>. Pure display mapping — no schema
/// or extraction changes.
/// </summary>
public enum SystemFamilyBadge
{
    /// <summary>Loadable types, single-family categories (pipes, floors,
    /// roofs, ...), Unknown discriminators and legacy pre-V27 rows.</summary>
    None,

    /// <summary>Duct.Round / FlexDuctRound — circle profile.</summary>
    ShapeRound,

    /// <summary>Duct.Rectangular / FlexDuctRectangular.</summary>
    ShapeRectangular,

    /// <summary>Duct.Oval.</summary>
    ShapeOval,

    /// <summary>Conduit/CableTray.WithFittings.</summary>
    Fittings,

    /// <summary>Conduit/CableTray.WithoutFittings.</summary>
    FittingsNone,

    WallBasic,
    WallStacked,
    WallCurtain,

    StairsAssembled,
    StairsCastInPlace,
    StairsPrecast,
}

/// <summary>
/// Issue #203: maps a persisted <see cref="SystemFamilyKeys"/> token to its
/// display badge. <see cref="SystemFamilyBadge.None"/> for everything the
/// tree should not badge (Single/Unknown/legacy/empty keys) — the fallback
/// must stay silent: an unknown future discriminator degrades to no badge
/// rather than a wrong one.
/// </summary>
public static class SystemFamilyBadgeMap
{
    public static SystemFamilyBadge Resolve(string? familyKey) => familyKey switch
    {
        SystemFamilyKeys.DuctRound => SystemFamilyBadge.ShapeRound,
        SystemFamilyKeys.FlexDuctRound => SystemFamilyBadge.ShapeRound,
        SystemFamilyKeys.DuctRectangular => SystemFamilyBadge.ShapeRectangular,
        SystemFamilyKeys.FlexDuctRectangular => SystemFamilyBadge.ShapeRectangular,
        SystemFamilyKeys.DuctOval => SystemFamilyBadge.ShapeOval,
        SystemFamilyKeys.ConduitWithFittings => SystemFamilyBadge.Fittings,
        SystemFamilyKeys.CableTrayWithFittings => SystemFamilyBadge.Fittings,
        SystemFamilyKeys.ConduitWithoutFittings => SystemFamilyBadge.FittingsNone,
        SystemFamilyKeys.CableTrayWithoutFittings => SystemFamilyBadge.FittingsNone,
        SystemFamilyKeys.WallBasic => SystemFamilyBadge.WallBasic,
        SystemFamilyKeys.WallStacked => SystemFamilyBadge.WallStacked,
        SystemFamilyKeys.WallCurtain => SystemFamilyBadge.WallCurtain,
        SystemFamilyKeys.StairsAssembled => SystemFamilyBadge.StairsAssembled,
        SystemFamilyKeys.StairsCastInPlace => SystemFamilyBadge.StairsCastInPlace,
        SystemFamilyKeys.StairsPrecast => SystemFamilyBadge.StairsPrecast,
        _ => SystemFamilyBadge.None,
    };
}

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Computes the locale-invariant system family key (Issue #190, ADR-064).
/// <c>ElementType.FamilyName</c> is a localized string (revitapidocs:
/// "This value is a localized string describing the family") — on a shared
/// catalog a type imported from an EN-Revit never name-matches in a
/// RU-Revit. The key replaces it as the sync/stale identity via a
/// per-category discriminator API:
/// <list type="bullet">
/// <item><c>ConduitType</c>/<c>CableTrayType</c> →
/// <c>IsWithFitting</c> (property, since Revit 2015).</item>
/// <item><c>WallType</c> → <c>Kind</c> (<see cref="WallKind"/>).</item>
/// <item><c>StairsType</c> → <c>ConstructionMethod</c>
/// (<see cref="StairsConstructionMethod"/>, since Revit 2013).</item>
/// <item><c>DuctType</c> → <c>Shape</c> (#215, FHV7 —
/// <c>MEPCurveType.Shape : ConnectorProfileType</c>; the category has THREE
/// system families — round/rectangular/oval — not one).</item>
/// <item><c>FlexDuctType</c> → <c>Shape</c> (owner stress test 2026-08-30 —
/// the category has TWO system families: круглого / прямоугольного сечения).
/// </item>
/// <item>Everything else → <see cref="SystemFamilyKeys.SingleFamily"/> (one
/// system family per category: pipes, flex pipes, floors, roofs, ceilings,
/// railings, insulations, wires — foundation slabs live in
/// OST_StructuralFoundation, not in OST_Floors).</item>
/// </list>
/// All discriminators are verified on revitapidocs 2021–2027 — no version
/// gate required. Enum names are culture-invariant by CLR definition.
/// </summary>
public static class SystemFamilyKeyResolver
{
    public static string Resolve(ElementType? type)
    {
        return type switch
        {
            ConduitType conduit => conduit.IsWithFitting
                ? SystemFamilyKeys.ConduitWithFittings
                : SystemFamilyKeys.ConduitWithoutFittings,
            CableTrayType tray => tray.IsWithFitting
                ? SystemFamilyKeys.CableTrayWithFittings
                : SystemFamilyKeys.CableTrayWithoutFittings,
            WallType wall => wall.Kind switch
            {
                WallKind.Basic => SystemFamilyKeys.WallBasic,
                WallKind.Curtain => SystemFamilyKeys.WallCurtain,
                WallKind.Stacked => SystemFamilyKeys.WallStacked,
                _ => SystemFamilyKeys.WallUnknown,
            },
            StairsType stairs => stairs.ConstructionMethod switch
            {
                StairsConstructionMethod.Assembled => SystemFamilyKeys.StairsAssembled,
                StairsConstructionMethod.CastInPlace => SystemFamilyKeys.StairsCastInPlace,
                StairsConstructionMethod.Precast => SystemFamilyKeys.StairsPrecast,
                _ => SystemFamilyKeys.StairsUnknown,
            },
            Autodesk.Revit.DB.Mechanical.DuctType duct => duct.Shape switch
            {
                ConnectorProfileType.Round => SystemFamilyKeys.DuctRound,
                ConnectorProfileType.Rectangular => SystemFamilyKeys.DuctRectangular,
                ConnectorProfileType.Oval => SystemFamilyKeys.DuctOval,
                _ => SystemFamilyKeys.DuctUnknown,
            },
            Autodesk.Revit.DB.Mechanical.FlexDuctType flexDuct => flexDuct.Shape switch
            {
                ConnectorProfileType.Round => SystemFamilyKeys.FlexDuctRound,
                ConnectorProfileType.Rectangular => SystemFamilyKeys.FlexDuctRectangular,
                _ => SystemFamilyKeys.FlexDuctUnknown,
            },
            _ => SystemFamilyKeys.SingleFamily,
        };
    }
}

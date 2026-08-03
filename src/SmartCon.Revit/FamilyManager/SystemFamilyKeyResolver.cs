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
/// <item>Everything else → <see cref="SystemFamilyKeys.SingleFamily"/> (one
/// system family per category: pipes, ducts, floors, roofs, ceilings,
/// railings, insulations, wires, flex curves — foundation slabs live in
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
                _ => SystemFamilyKeys.SingleFamily,
            },
            _ => SystemFamilyKeys.SingleFamily,
        };
    }
}

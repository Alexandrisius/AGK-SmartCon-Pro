using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class RevitFamilySnapshotExtractor
{
    /// <summary>
    /// Compound structure (layer stack) of a host type — Basic walls,
    /// floors, roofs, ceilings (ADR-056). <c>null</c> for non-host types
    /// and for hosts without a compound structure (stacked/curtain
    /// walls): both are deterministic canonical states, distinct from
    /// each other only by the type itself.
    /// </summary>
    private static CompoundStructureSnapshot? ExtractCompoundStructure(
        ElementType elementType, Document doc)
    {
        if (elementType is not HostObjAttributes hostType)
            return null;

        try
        {
            using var compoundStructure = hostType.GetCompoundStructure();
            if (compoundStructure is null)
                return null;

            var layers = compoundStructure.GetLayers();
            var variableLayerIndex = compoundStructure.VariableLayerIndex;
            var exteriorShells = compoundStructure.GetNumberOfShellLayers(ShellLayerType.Exterior);
            var interiorShells = compoundStructure.GetNumberOfShellLayers(ShellLayerType.Interior);
            var snapshots = new List<CompoundLayerSnapshot>(layers.Count);

            for (var i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                string? materialName = null;
                try
                {
                    if (layer.MaterialId is not null && layer.MaterialId != ElementId.InvalidElementId)
                    {
                        materialName = doc.GetElement(layer.MaterialId)?.Name;
                    }
                }
                catch
                {
                    materialName = null;
                }

                // Wrapping participation is defined only for shell layers
                // (leading exterior + trailing interior ones).
                var isShellLayer = i < exteriorShells || i >= layers.Count - interiorShells;
                var participatesInWrapping = false;
                if (isShellLayer)
                {
                    try { participatesInWrapping = compoundStructure.ParticipatesInWrapping(i); }
                    catch { /* core-layer guard — stays false */ }
                }

                var layerCapFlag = false;
                try { layerCapFlag = layer.LayerCapFlag; }
                catch { /* best-effort flag */ }

                snapshots.Add(new CompoundLayerSnapshot(
                    Function: (int)layer.Function,
                    Width: layer.Width,
                    MaterialName: materialName,
                    IsVariable: i == variableLayerIndex,
                    LayerCapFlag: layerCapFlag,
                    ParticipatesInWrapping: participatesInWrapping));
            }

            var endCap = -1;
            var openingWrapping = -1;
            try { endCap = (int)compoundStructure.EndCap; } catch { /* stays unknown */ }
            try { openingWrapping = (int)compoundStructure.OpeningWrapping; } catch { /* stays unknown */ }

            return new CompoundStructureSnapshot(
                ExteriorShellLayerCount: exteriorShells,
                InteriorShellLayerCount: interiorShells,
                Layers: snapshots,
                StructuralMaterialIndex: compoundStructure.StructuralMaterialIndex,
                EndCap: endCap,
                OpeningWrapping: openingWrapping);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"CompoundStructure read failed for type '{elementType.Name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Stairs subtype references by NAME — FHV4 hash identity (#184,
    /// ADR-065). <c>null</c> for non-stairs types. The cut mark type has
    /// no dedicated property — it is read via the
    /// <c>STAIRSTYPE_CUTMARK_TYPE</c> built-in parameter (Autodesk
    /// Stairs Annotations guide).
    /// </summary>
    private static StairsSubtypesSnapshot? ExtractStairsSubtypes(
        ElementType elementType, Document doc)
    {
        if (elementType is not StairsType stairsType)
            return null;

        try
        {
            string? NameOf(ElementId? id)
            {
                try
                {
                    return id is null || id == ElementId.InvalidElementId
                        ? null
                        : doc.GetElement(id)?.Name;
                }
                catch
                {
                    return null;
                }
            }

            ElementId? cutMarkId = null;
            try
            {
                cutMarkId = stairsType
                    .get_Parameter(BuiltInParameter.STAIRSTYPE_CUTMARK_TYPE)
                    ?.AsElementId();
            }
            catch { /* no cut mark parameter — null */ }

            return new StairsSubtypesSnapshot(
                RunTypeName: NameOf(stairsType.RunType),
                LandingTypeName: NameOf(stairsType.LandingType),
                LeftSupportTypeName: NameOf(stairsType.LeftSideSupportType),
                RightSupportTypeName: NameOf(stairsType.RightSideSupportType),
                MiddleSupportTypeName: NameOf(stairsType.MiddleSupportType),
                CutMarkTypeName: NameOf(cutMarkId));
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Stairs subtypes read failed for type '{elementType.Name}': {ex.Message}");
            return null;
        }
    }

    private static bool IsValidId(ElementId? id) =>
        id is not null && id != ElementId.InvalidElementId;

    /// <summary>
    /// FHV5: wire settings identity summary — the material/temperature
    /// rating/insulation/max-size/conduit references of a <see cref="WireType"/>
    /// plus the neutral scalars. These are API properties, NOT element
    /// parameters, so the generic pipeline never sees them (manual test
    /// 2026-08-04: a material change on a wire type did not sync). On
    /// Revit ≤2025 they are backed by the <c>ElectricalSetting</c> object
    /// graph (WireMaterialType → TemperatureRatingType → InsulationType/
    /// WireSize); Revit 2026+ replaced it with the flat Conductor* model —
    /// WireType.WireMaterial/TemperatureRating/Insulation are ElementIds
    /// resolved through the Conductor* statics (the objects are NOT
    /// Element-derived, <c>doc.GetElement</c> does not work for them) and
    /// MaxSize is the ConductorSize name itself (#233). The snapshot fields
    /// are names on every version, so the canonical WIRE string is
    /// byte-identical across R25/R26 (FHV22).
    /// </summary>
    private static WireSettingsSnapshot? ExtractWireSettings(ElementType elementType)
    {
        if (elementType is not WireType wireType)
            return null;

        try
        {
#if REVIT2026_OR_GREATER
            var doc = wireType.Document;
            return new WireSettingsSnapshot(
                MaterialName: RevitConductorCompat.MaterialName(doc, wireType.WireMaterial),
                TemperatureRatingName: RevitConductorCompat.TemperatureRatingName(doc, wireType.TemperatureRating),
                InsulationName: RevitConductorCompat.InsulationName(doc, wireType.Insulation),
                // An unset MaxSize is an empty string on 2026+ — normalize to
                // null so the WIRE canon matches the ≤2025 object-null case.
                MaxSizeName: string.IsNullOrEmpty(wireType.MaxSize) ? null : wireType.MaxSize,
                ConduitName: wireType.Conduit?.Name,
                NeutralMultiplier: wireType.NeutralMultiplier,
                NeutralRequired: wireType.NeutralRequired);
#else
            return new WireSettingsSnapshot(
                MaterialName: wireType.WireMaterial?.Name,
                TemperatureRatingName: wireType.TemperatureRating?.Name,
                InsulationName: wireType.Insulation?.Name,
                MaxSizeName: wireType.MaxSize?.Size,
                ConduitName: wireType.Conduit?.Name,
                NeutralMultiplier: wireType.NeutralMultiplier,
                NeutralRequired: wireType.NeutralRequired);
#endif
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Wire settings read failed for type '{elementType.Name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Railing structure identity summary — FHV4 hash content (ADR-065):
    /// top rail, handrails, the non-continuous rail list and the baluster
    /// placement scalars. Element references are carried by NAME (user
    /// content, locale-stable); baluster families are family-qualified
    /// ("{Family}:{Type}") like routing part names. <c>null</c> for
    /// non-railing types.
    /// </summary>
    private static RailingStructureSnapshot? ExtractRailingStructure(
        ElementType elementType, Document doc)
    {
        if (elementType is not RailingType railingType)
            return null;

        try
        {
            string? NameOf(ElementId? id)
            {
                try
                {
                    return id is null || id == ElementId.InvalidElementId
                        ? null
                        : doc.GetElement(id)?.Name;
                }
                catch
                {
                    return null;
                }
            }

            string? FamilyQualifiedNameOf(ElementId? id)
            {
                try
                {
                    if (id is null || id == ElementId.InvalidElementId)
                        return null;
                    return doc.GetElement(id) switch
                    {
                        FamilySymbol symbol => $"{symbol.Family?.Name}:{symbol.Name}",
                        var element => element?.Name,
                    };
                }
                catch
                {
                    return null;
                }
            }

            var rails = new List<RailingRailSnapshot>();
            using (var railStructure = railingType.RailStructure)
            {
                if (railStructure is not null)
                {
                    var railCount = railStructure.GetNonContinuousRailCount();
                    for (var i = 0; i < railCount; i++)
                    {
                        using var rail = railStructure.GetNonContinuousRail(i);
                        if (rail is null) continue;
                        rails.Add(new RailingRailSnapshot(
                            rail.Name ?? string.Empty,
                            rail.Height,
                            rail.Offset,
                            FamilyQualifiedNameOf(rail.ProfileId),
                            NameOf(rail.MaterialId)));
                    }
                }
            }

            RailingBalusterSnapshot balusters;
            using (var placement = railingType.BalusterPlacement)
            {
                if (placement is not null)
                {
                    var balusterNames = new List<string?>();
                    double patternLength = 0;
                    var justification = -1;
                    var breakPattern = -1;
                    using (var pattern = placement.BalusterPattern)
                    {
                        if (pattern is not null)
                        {
                            patternLength = pattern.Length;
                            justification = (int)pattern.DistributionJustification;
                            breakPattern = (int)pattern.BreakPattern;
                            var balusterCount = pattern.GetBalusterCount();
                            for (var i = 0; i < balusterCount; i++)
                            {
                                using var baluster = pattern.GetBaluster(i);
                                balusterNames.Add(baluster is null
                                    ? null
                                    : FamilyQualifiedNameOf(baluster.BalusterFamilyId));
                            }
                        }
                    }

                    balusters = new RailingBalusterSnapshot(
                        PatternLength: patternLength,
                        DistributionJustification: justification,
                        BreakPattern: breakPattern,
                        BalusterFamilyNames: balusterNames,
                        UseBalusterPerTreadOnStairs: placement.UseBalusterPerTreadOnStairs,
                        BalusterPerTreadNumber: placement.BalusterPerTreadNumber,
                        BalusterPerTreadFamilyName: FamilyQualifiedNameOf(placement.BalusterPerTreadFamilyId));
                }
                else
                {
                    balusters = new RailingBalusterSnapshot(
                        0, -1, -1, [], false, 0, null);
                }
            }

            // Handrail-less railings: the handrail height/offset/position
            // getters THROW "The rail has no primary/secondary hand rail"
            // (stress test 2026-08-04 — the whole structure read aborted and
            // the RAILING hash section was lost). Guard each group by the
            // handrail reference; null = no handrail (deterministic state).
            var hasPrimary = IsValidId(railingType.PrimaryHandrailType);
            var hasSecondary = IsValidId(railingType.SecondaryHandrailType);

            return new RailingStructureSnapshot(
                TopRailTypeName: NameOf(railingType.TopRailType),
                TopRailHeight: railingType.TopRailHeight,
                PrimaryHandrailTypeName: NameOf(railingType.PrimaryHandrailType),
                PrimaryHandrailHeight: hasPrimary ? railingType.PrimaryHandrailHeight : null,
                PrimaryHandrailLateralOffset: hasPrimary ? railingType.PrimaryHandrailLateralOffset : null,
                PrimaryHandrailPosition: hasPrimary ? (int)railingType.PrimaryHandRailPosition : null,
                SecondaryHandrailTypeName: NameOf(railingType.SecondaryHandrailType),
                SecondaryHandrailHeight: hasSecondary ? railingType.SecondaryHandrailHeight : null,
                SecondaryHandrailLateralOffset: hasSecondary ? railingType.SecondaryHandrailLateralOffset : null,
                SecondaryHandrailPosition: hasSecondary ? (int)railingType.SecondaryHandRailPosition : null,
                Rails: rails,
                Balusters: balusters);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Railing structure read failed for type '{elementType.Name}': {ex.Message}");
            return null;
        }
    }
}

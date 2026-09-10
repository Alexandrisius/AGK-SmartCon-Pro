using System.Collections;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using Electrical = Autodesk.Revit.DB.Electrical;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class SystemTypeSyncService
{
    /// <summary>
    /// #184 (ADR-065, вариант Б): sync the subtype references of a stairs
    /// type — run/landing/side supports/middle support/cut mark. Each
    /// referenced subtype is found in the target by (class, name) or
    /// created by duplicating a same-class prototype; its own parameters
    /// are written (live read from the mini-project); then the reference
    /// is assigned on the target stairs type. Missing prototype family in
    /// the project → Warn + NotConverged (never a cross-family write).
    /// </summary>
    private int SyncStairsSubtypes(
        Document sourceDoc,
        Document doc,
        StairsType source,
        StairsType target,
        Dictionary<string, ElementId?> elementIdCache)
    {
        var notConverged = 0;
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.RunType,
            typeof(StairsRunType), null, "RunType", id => target.RunType = id, elementIdCache);
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.LandingType,
            typeof(StairsLandingType), null, "LandingType", id => target.LandingType = id, elementIdCache);
        // Support types have no dedicated API class — their elements live in
        // OST_StairsStringerCarriage (RevitLookup/Autodesk forums: the
        // OST_StairsSupports category object exists but no element carries
        // it — collecting by it finds NOTHING), so they are collected by
        // that category.
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.LeftSideSupportType,
            null, BuiltInCategory.OST_StairsStringerCarriage, "LeftSideSupportType", id => target.LeftSideSupportType = id, elementIdCache);
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.RightSideSupportType,
            null, BuiltInCategory.OST_StairsStringerCarriage, "RightSideSupportType", id => target.RightSideSupportType = id, elementIdCache);
        // The setters throw when the target type has no middle supports /
        // the support style is none (revitapidocs InvalidOperationException)
        // — guard on the CURRENT state (parameters were already written).
        if (target.HasMiddleSupports)
        {
            notConverged += SyncReferencedSubtype(sourceDoc, doc, source.MiddleSupportType,
                null, BuiltInCategory.OST_StairsStringerCarriage, "MiddleSupportType", id => target.MiddleSupportType = id, elementIdCache);
        }
        else if (IsValidId(source.MiddleSupportType))
        {
            // ADR-065 §5 — never skip silently: the reference HAS a middle
            // support but the target's "Middle Support" flag stayed off
            // (its parameter write was skipped/failed) — report the residue.
            SmartConLogger.Warn(
                $"Stairs '{target.Name}': MiddleSupportType not synced — target HasMiddleSupports=false " +
                $"while the reference uses '{source.MiddleSupportType}'. " +
                "[Action: enable «Промежуточные опоры» on the target stairs type and re-run «Обновить»]");
            notConverged++;
        }

        var cutMarkParam = target.get_Parameter(BuiltInParameter.STAIRSTYPE_CUTMARK_TYPE);
        if (cutMarkParam is not null && !cutMarkParam.IsReadOnly)
        {
            var sourceCutMarkId = source.get_Parameter(BuiltInParameter.STAIRSTYPE_CUTMARK_TYPE)?.AsElementId();
            notConverged += SyncReferencedSubtype(sourceDoc, doc, sourceCutMarkId,
                typeof(CutMarkType), null, "CutMarkType", id => cutMarkParam.Set(id), elementIdCache);
        }

        // Read-back ground truth (manual test 2026-08-04: supports reported
        // as not syncing despite a clean run) — what the target type
        // actually references AFTER all assignments.
        SmartConLogger.Debug(
            $"Stairs '{target.Name}' subtype read-back: " +
            $"Run='{ResolveElementName(doc, target.RunType)}', Landing='{ResolveElementName(doc, target.LandingType)}', " +
            $"Left='{ResolveElementName(doc, target.LeftSideSupportType)}', " +
            $"Right='{ResolveElementName(doc, target.RightSideSupportType)}', " +
            $"Middle='{(target.HasMiddleSupports ? ResolveElementName(doc, target.MiddleSupportType) : "<none>")}' " +
            $"(source: Run='{ResolveElementName(sourceDoc, source.RunType)}', " +
            $"Landing='{ResolveElementName(sourceDoc, source.LandingType)}', " +
            $"Left='{ResolveElementName(sourceDoc, source.LeftSideSupportType)}', " +
            $"Right='{ResolveElementName(sourceDoc, source.RightSideSupportType)}').");
        return notConverged;
    }

    /// <summary>
    /// Find-or-create a referenced subtype in the target by name (within
    /// its class or category), write its parameters (live read from the
    /// mini-project) and assign the reference. Missing prototype in the
    /// project → Warn + NotConverged (never a cross-family write).
    /// </summary>
    private int SyncReferencedSubtype(
        Document sourceDoc,
        Document doc,
        ElementId? sourceSubtypeId,
        Type? subtypeClass,
        BuiltInCategory? subtypeCategory,
        string slotName,
        Action<ElementId> assign,
        Dictionary<string, ElementId?> elementIdCache)
    {
        if (sourceSubtypeId is null || sourceSubtypeId == ElementId.InvalidElementId)
            return 0;

        var sourceSubtype = sourceDoc.GetElement(sourceSubtypeId) as ElementType;
        if (sourceSubtype is null)
        {
            SmartConLogger.Warn(
                $"Subtype ({slotName}) unreadable in the source mini-project. " +
                "[Action: reimport the category into the catalog]");
            return 1;
        }

        var targetSubtype = CollectSubtypeCandidates(doc, subtypeClass, subtypeCategory)
            .FirstOrDefault(t => string.Equals(t.Name, sourceSubtype.Name, StringComparison.OrdinalIgnoreCase));
        if (targetSubtype is null)
        {
            var prototype = CollectSubtypeCandidates(doc, subtypeClass, subtypeCategory)
                .FirstOrDefault();
            if (prototype is null)
            {
                var scope = subtypeClass?.Name ?? subtypeCategory?.ToString() ?? "?";
                SmartConLogger.Warn(
                    $"Subtype '{sourceSubtype.Name}' ({slotName}): no {scope} prototype " +
                    "in the project — the subtype family cannot be created via the API. " +
                    "[Action: place any element using this subtype family in the project, then retry]");
                return 1;
            }
            targetSubtype = prototype.Duplicate(sourceSubtype.Name);
            SmartConLogger.Debug(
                $"Subtype '{sourceSubtype.Name}' ({slotName}): created in the project by duplicating '{prototype.Name}'.");
        }
        else
        {
            SmartConLogger.Debug(
                $"Subtype '{sourceSubtype.Name}' ({slotName}): matched existing project subtype #{targetSubtype.Id}.");
        }

        var subtypeTemplate = _snapshotExtractor.ExtractSingleSystemType(sourceDoc, sourceSubtypeId);
        if (subtypeTemplate is not null)
        {
            WriteParameters(sourceDoc, doc, targetSubtype, subtypeTemplate, elementIdCache);
        }

        try
        {
            assign(targetSubtype.Id);
            SmartConLogger.Debug(
                $"Subtype reference assigned ({slotName} = '{sourceSubtype.Name}', id=#{targetSubtype.Id}).");
            return 0;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Stairs subtype assignment failed ({slotName} = '{sourceSubtype.Name}'): {ex.Message} " +
                "[Action: check the log; the subtype data is synced, only the reference assignment failed]");
            return 1;
        }
    }

    /// <summary>
    /// ADR-065: sync the railing structure — top rail / handrails (type by
    /// name + scalars), the non-continuous rail list (cleared and rebuilt
    /// from the reference; profile by family-qualified name, material by
    /// name with the material-sync fallback) and the baluster placement
    /// scalars. Every rejected member counts as NotConverged — no silent
    /// skips.
    /// </summary>
    private int SyncRailingStructure(
        Document sourceDoc,
        Document doc,
        RailingType source,
        RailingType target,
        Dictionary<string, ElementId?> elementIdCache)
    {
        var notConverged = 0;

        notConverged += TrySetRailingMember("TopRailHeight", () => target.TopRailHeight = source.TopRailHeight);
        // The handrail height/offset properties on RailingType are READ-ONLY
        // (revitapidocs) — they follow the assigned handrail TYPE. So the
        // handrail/top-rail types are synced as referenced subtypes (their
        // own parameters included) and only the reference + position are
        // assigned on the railing type. The position setter throws "The
        // rail has no primary/secondary hand rail" when the railing has no
        // such handrail at all — skip it then (stress test 2026-08-04:
        // false NotConverged noise on handrail-less railings).
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.TopRailType,
            typeof(TopRailType), null, "TopRailType", id => target.TopRailType = id, elementIdCache);
        if (IsValidId(source.PrimaryHandrailType))
        {
            notConverged += TrySetRailingMember("PrimaryHandRailPosition", () => target.PrimaryHandRailPosition = source.PrimaryHandRailPosition);
        }
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.PrimaryHandrailType,
            typeof(HandRailType), null, "PrimaryHandrailType", id => target.PrimaryHandrailType = id, elementIdCache);
        if (IsValidId(source.SecondaryHandrailType))
        {
            notConverged += TrySetRailingMember("SecondaryHandRailPosition", () => target.SecondaryHandRailPosition = source.SecondaryHandRailPosition);
        }
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.SecondaryHandrailType,
            typeof(HandRailType), null, "SecondaryHandrailType", id => target.SecondaryHandrailType = id, elementIdCache);

        using var sourceStructure = source.RailStructure;
        using var targetStructure = target.RailStructure;
        if (sourceStructure is not null && targetStructure is not null)
        {
            while (targetStructure.GetNonContinuousRailCount() > 0)
            {
                targetStructure.RemoveNonContinuousRail(0);
            }

            var railCount = sourceStructure.GetNonContinuousRailCount();
            for (var i = 0; i < railCount; i++)
            {
                using var sourceRail = sourceStructure.GetNonContinuousRail(i);
                if (sourceRail is null) continue;

                // AddNonContinuousRail throws ArgumentException (invalid/
                // duplicate name, height above the railing height — e.g.
                // when TopRailHeight was rejected above) — per-rail
                // granularity: one bad rail must not fail the whole type.
                NonContinuousRailInfo? newRail;
                try
                {
                    targetStructure.AddNonContinuousRail(sourceRail.Name, sourceRail.Height, sourceRail.Offset);
                    newRail = targetStructure.GetNonContinuousRail(
                        targetStructure.GetNonContinuousRailCount() - 1);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"Railing rail '{sourceRail.Name}' rejected: {ex.Message} " +
                        "[Action: check the rail name/height against the railing type height]");
                    notConverged++;
                    continue;
                }

                using (newRail)
                {
                    if (newRail is null)
                    {
                        notConverged++;
                        continue;
                    }

                    var profileName = ResolveQualifiedSymbolName(sourceDoc, sourceRail.ProfileId);
                    if (profileName is not null)
                    {
                        var profileId = ResolveFamilySymbolByQualifiedName(doc, profileName);
                        if (profileId is not null)
                        {
                            notConverged += TrySetRailingMember($"Rail[{sourceRail.Name}].Profile",
                                () => newRail.ProfileId = profileId);
                        }
                        else
                        {
                            SmartConLogger.Warn(
                                $"Railing rail '{sourceRail.Name}': profile '{profileName}' not found in the project. " +
                                "[Action: load the profile family into the project, then retry the sync]");
                            notConverged++;
                        }
                    }

                    var materialName = ResolveElementName(sourceDoc, sourceRail.MaterialId);
                    if (materialName is not null)
                    {
                        var materialId = ResolveElementByName(doc, materialName)
                            ?? _materialSync.SyncMaterial(sourceDoc, doc, materialName);
                        if (materialId is not null)
                        {
                            notConverged += TrySetRailingMember($"Rail[{sourceRail.Name}].Material",
                                () => newRail.MaterialId = materialId);
                        }
                        else
                        {
                            notConverged++;
                        }
                    }
                }
            }
        }

        using var sourcePlacement = source.BalusterPlacement;
        using var targetPlacement = target.BalusterPlacement;
        if (sourcePlacement is not null && targetPlacement is not null)
        {
            notConverged += TrySetRailingMember("UseBalusterPerTreadOnStairs",
                () => targetPlacement.UseBalusterPerTreadOnStairs = sourcePlacement.UseBalusterPerTreadOnStairs);
            notConverged += TrySetRailingMember("BalusterPerTreadNumber",
                () => targetPlacement.BalusterPerTreadNumber = sourcePlacement.BalusterPerTreadNumber);
            notConverged += TrySetRailingMember("BalusterPerTreadFamily", () =>
            {
                var qualified = ResolveQualifiedSymbolName(sourceDoc, sourcePlacement.BalusterPerTreadFamilyId);
                if (qualified is null) return;
                var id = ResolveFamilySymbolByQualifiedName(doc, qualified);
                if (id is null)
                {
                    // No silent skips (ADR-065 §5): a missing baluster
                    // family is a visible non-convergence.
                    throw new InvalidOperationException(
                        $"baluster per-tread family '{qualified}' not found in the project");
                }
                targetPlacement.BalusterPerTreadFamilyId = id;
            });

            using var sourcePattern = sourcePlacement.BalusterPattern;
            using var targetPattern = targetPlacement.BalusterPattern;
            if (sourcePattern is not null && targetPattern is not null)
            {
                // BalusterPattern.Length is read-only (computed from the
                // baluster list) — the scalars below carry the content.
                notConverged += TrySetRailingMember("BalusterPattern.DistributionJustification",
                    () => targetPattern.DistributionJustification = sourcePattern.DistributionJustification);
                notConverged += TrySetRailingMember("BalusterPattern.BreakPattern",
                    () => targetPattern.BreakPattern = sourcePattern.BreakPattern);
                notConverged += TrySetRailingMember("BalusterPattern.EndSpace",
                    () => targetPattern.EndSpace = sourcePattern.EndSpace);
                notConverged += TrySetRailingMember("BalusterPattern.ExcessLengthFillSpacing",
                    () => targetPattern.ExcessLengthFillSpacing = sourcePattern.ExcessLengthFillSpacing);
            }
        }

        return notConverged;
    }

    private static bool IsValidId(ElementId? id) =>
        id is not null && id != ElementId.InvalidElementId;

    private static List<ElementType> CollectSubtypeCandidates(
        Document doc, Type? subtypeClass, BuiltInCategory? subtypeCategory)
    {
        IEnumerable<ElementType> candidates = new FilteredElementCollector(doc)
            .WhereElementIsElementType()
            .Cast<ElementType>();
        if (subtypeClass is not null)
        {
            candidates = candidates.Where(t => subtypeClass.IsInstanceOfType(t));
        }
        if (subtypeCategory is not null)
        {
            var categoryId = Core.Compatibility.ElementIdCompat.Create((int)subtypeCategory.Value);
            candidates = candidates.Where(t => t.Category is not null && t.Category.Id == categoryId);
        }
        return candidates.ToList();
    }

    private static int TrySetRailingMember(string memberName, Action write)
    {
        try
        {
            write();
            return 0;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Railing structure member '{memberName}' rejected: {ex.Message} " +
                "[Action: see the log; the remaining members were applied]");
            return 1;
        }
    }

    private static string? ResolveElementName(Document doc, ElementId? id)
    {
        if (id is null || id == ElementId.InvalidElementId) return null;
        try { return doc.GetElement(id)?.Name; }
        catch { return null; }
    }

    private static string? ResolveQualifiedSymbolName(Document doc, ElementId? id)
    {
        if (id is null || id == ElementId.InvalidElementId) return null;
        try
        {
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

    private static ElementId? ResolveFamilySymbolByQualifiedName(Document doc, string qualifiedName)
    {
        var separator = qualifiedName.IndexOf(':');
        if (separator <= 0) return null;
        var familyName = qualifiedName[..separator];
        var typeName = qualifiedName[(separator + 1)..];
        return new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .FirstOrDefault(s =>
                string.Equals(s.Family?.Name, familyName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase))
            ?.Id;
    }
}

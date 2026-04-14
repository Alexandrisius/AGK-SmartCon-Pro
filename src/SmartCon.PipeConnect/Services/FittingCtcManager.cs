using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

using static SmartCon.Core.Units;

using FittingCardItem = SmartCon.PipeConnect.ViewModels.FittingCardItem;

namespace SmartCon.PipeConnect.Services;

public sealed class FittingCtcManager(
    IConnectorService connSvc,
    IFittingMappingRepository mappingRepo,
    IDialogService dialogSvc,
    IFamilyConnectorService familyConnSvc,
    VirtualCtcStore virtualCtcStore)
{
    public ConnectionTypeCode ResolveDynamicTypeFromRule(
        FittingMappingRule? rule, ConnectionTypeCode staticCtc)
    {
        if (rule is null) return default;
        if (rule.FromType.Value == staticCtc.Value)
            return rule.ToType;
        return rule.FromType;
    }

    public ConnectorProxy? RefreshWithCtcOverride(
        Document doc, ElementId elemId, int connIdx)
    {
        var proxy = connSvc.RefreshConnector(doc, elemId, connIdx);
        if (proxy is null) return null;
        var ctc = virtualCtcStore.Get(elemId, connIdx);
        return ctc.HasValue ? proxy with { ConnectionTypeCode = ctc.Value } : proxy;
    }

    public IReadOnlyDictionary<int, ConnectionTypeCode> GuessCtcForFitting(
        Document doc, ElementId fittingId,
        ConnectorProxy staticConnector, ConnectorProxy dynamicFallback, ConnectorProxy? activeDynamic)
        => GuessCtcForElement(doc, fittingId, isReducer: false, staticConnector, dynamicFallback, activeDynamic);

    public IReadOnlyDictionary<int, ConnectionTypeCode> GuessCtcForReducer(
        Document doc, ElementId reducerId,
        ConnectorProxy staticConnector, ConnectorProxy dynamicFallback, ConnectorProxy? activeDynamic)
        => GuessCtcForElement(doc, reducerId, isReducer: true, staticConnector, dynamicFallback, activeDynamic);

    public IReadOnlyDictionary<int, ConnectionTypeCode> GuessCtcForElement(
        Document doc, ElementId elementId, bool isReducer,
        ConnectorProxy staticConnector, ConnectorProxy dynamicFallback, ConnectorProxy? activeDynamic)
    {
        var label = isReducer ? "Reducer" : "Fitting";
        var conns = connSvc.GetAllConnectors(doc, elementId);

        bool allDefined = conns.Count >= 2 && conns.All(c => c.ConnectionTypeCode.IsDefined);
        if (allDefined)
        {
            foreach (var c in conns)
                virtualCtcStore.Set(elementId, c.ConnectorIndex, c.ConnectionTypeCode);
            SmartConLogger.Info($"[VirtualCTC] {label} {elementId.Value}: CTC already defined → " +
                string.Join(", ", conns.Select(c => $"conn[{c.ConnectorIndex}]={c.ConnectionTypeCode.Value}")));
            return virtualCtcStore.GetOverridesForElement(elementId);
        }

        var staticCTC = staticConnector.ConnectionTypeCode;
        var dynamicCTC = (activeDynamic ?? dynamicFallback).ConnectionTypeCode;
        var mappingRules = mappingRepo.GetMappingRules();

        var (ctcForStaticSide, ctcForDynamicSide) = isReducer
            ? CtcGuesser.GuessReducerCtc(staticCTC, dynamicCTC, mappingRules)
            : CtcGuesser.GuessAdapterCtc(staticCTC, dynamicCTC, mappingRules);

        if (conns.Count >= 2)
        {
            var staticOrigin = staticConnector.Origin;
            var connForStatic = conns
                .OrderBy(c => c.Origin.DistanceTo(staticOrigin))
                .ThenBy(c => c.ConnectorIndex)
                .First();
            var connForDynamic = conns.First(c => c.ConnectorIndex != connForStatic.ConnectorIndex);

            virtualCtcStore.Set(elementId, connForStatic.ConnectorIndex, ctcForStaticSide);
            virtualCtcStore.Set(elementId, connForDynamic.ConnectorIndex, ctcForDynamicSide);
            SmartConLogger.Info($"[VirtualCTC] {label} {elementId.Value} (guessed): " +
                $"conn[{connForStatic.ConnectorIndex}]={ctcForStaticSide.Value}→static(R={connForStatic.Radius * FeetToMm:F1}mm), " +
                $"conn[{connForDynamic.ConnectorIndex}]={ctcForDynamicSide.Value}→dynamic(R={connForDynamic.Radius * FeetToMm:F1}mm)");
        }

        return virtualCtcStore.GetOverridesForElement(elementId);
    }

    public List<FittingCtcSetupItem> BuildCtcItemsFromVirtualStore(
        Document doc, ElementId elementId, IReadOnlyList<ConnectorTypeDefinition> types)
    {
        var conns = connSvc.GetAllConnectors(doc, elementId);
        var items = new List<FittingCtcSetupItem>();

        foreach (var c in conns)
        {
            var vCtc = virtualCtcStore.Get(elementId, c.ConnectorIndex);
            var selectedType = vCtc.HasValue
                ? types.FirstOrDefault(t => t.Code == vCtc.Value.Value)
                : (c.ConnectionTypeCode.IsDefined
                    ? types.FirstOrDefault(t => t.Code == c.ConnectionTypeCode.Value)
                    : null);

            double diamMm = c.Radius * 2.0 * FeetToMm;
            items.Add(new FittingCtcSetupItem
            {
                ConnectorIndex = c.ConnectorIndex,
                ParameterName = string.Empty,
                DiameterMm = diamMm,
                SelectedType = selectedType,
            });
        }

        return items;
    }

    public ConnectorTypeDefinition? FindTypeDef(ConnectionTypeCode ctc)
    {
        if (!ctc.IsDefined) return null;
        return mappingRepo.GetConnectorTypes().FirstOrDefault(t => t.Code == ctc.Value);
    }

    public void PromoteGuessedCtcToPendingWrites(
        Document doc, ElementId? currentFittingId, ElementId? primaryReducerId)
    {
        PromoteElementCtcToPendingWrites(doc, currentFittingId);
        PromoteElementCtcToPendingWrites(doc, primaryReducerId);
    }

    public void PromoteElementCtcToPendingWrites(Document doc, ElementId? elementId)
    {
        if (elementId is null) return;

        var overrides = virtualCtcStore.GetOverridesForElement(elementId);
        if (overrides.Count == 0) return;

        var conns = connSvc.GetAllConnectors(doc, elementId);
        bool allDefined = conns.Count >= 2 && conns.All(c => c.ConnectionTypeCode.IsDefined);
        if (allDefined)
        {
            bool allMatch = true;
            foreach (var c in conns)
            {
                var vCtc = virtualCtcStore.Get(elementId, c.ConnectorIndex);
                if (vCtc.HasValue && vCtc.Value.Value != c.ConnectionTypeCode.Value)
                {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch) return;

            SmartConLogger.Info($"[CTC] Virtual CTC differs from family CTC for {elementId.Value} — overwrite");
        }

        foreach (var (connIdx, ctc) in overrides)
        {
            var typeDef = FindTypeDef(ctc);
            if (typeDef is not null)
            {
                virtualCtcStore.Set(elementId, connIdx, ctc, typeDef);
                SmartConLogger.Info($"[CTC] Promoted guessed CTC {ctc.Value} → pending write for {elementId.Value}:{connIdx}");
            }
        }
    }

    public ConnectionTypeCode GetEffectiveConnectorCtc(ElementId elementId, ConnectorProxy conn)
    {
        var vCtc = virtualCtcStore.Get(elementId, conn.ConnectorIndex);
        return vCtc ?? conn.ConnectionTypeCode;
    }

    public (ConnectorProxy? ToStatic, ConnectorProxy? ToDynamic) ResolveConnectorSidesForElement(
        Document doc,
        ElementId elementId,
        IReadOnlyList<ConnectorProxy> conns,
        ConnectionTypeCode dynamicTypeCode,
        ConnectorProxy staticConnector)
    {
        var rules = mappingRepo.GetMappingRules();
        var staticTypeCode = staticConnector.ConnectionTypeCode;

        if (rules.Count > 0 && staticTypeCode.IsDefined && dynamicTypeCode.IsDefined)
        {
            var connCtcMap = conns
                .Select(c => (Conn: c, Ctc: GetEffectiveConnectorCtc(elementId, c)))
                .ToList();

            var validPairs = new List<(ConnectorProxy Fc1, ConnectorProxy Fc2, double Score)>();
            foreach (var left in connCtcMap)
            {
                if (!CtcGuesser.CanDirectConnect(left.Ctc, staticTypeCode, rules))
                    continue;

                var right = connCtcMap.FirstOrDefault(x =>
                    x.Conn.ConnectorIndex != left.Conn.ConnectorIndex
                    && CtcGuesser.CanDirectConnect(x.Ctc, dynamicTypeCode, rules));

                if (right.Conn is not null)
                {
                    double score = System.Math.Abs(left.Conn.Radius - staticConnector.Radius);
                    validPairs.Add((left.Conn, right.Conn, score));
                }
            }

            if (validPairs.Count > 0)
            {
                var best = validPairs.OrderBy(p => p.Score).First();
                return (best.Fc1, best.Fc2);
            }
        }

        var toStatic = conns
            .OrderBy(c => VectorUtils.DistanceTo(c.OriginVec3, staticConnector.OriginVec3))
            .FirstOrDefault();
        var toDynamic = conns.FirstOrDefault(c => c.ConnectorIndex != (toStatic?.ConnectorIndex ?? -1));
        return (toStatic, toDynamic);
    }

    public bool EnsureFittingCtcForInsert(
        Document doc, FittingCardItem fitting, ConnectorProxy staticConnector)
    {
        if (doc.IsModifiable) return true;

        var primary = fitting.PrimaryFitting;
        if (primary is null) return true;

        var symbol = FindFamilySymbol(doc, primary.FamilyName, primary.SymbolName);
        if (symbol is null) return true;

        if (IsFittingCtcDefined(doc, symbol)) return true;

        var types = mappingRepo.GetConnectorTypes();
        if (types.Count == 0) return true;

        var items = BuildConnectorItems(doc, symbol, types, fitting.Rule, staticConnector.ConnectionTypeCode);

        SmartConLogger.Info($"[CTC] Fitting '{symbol.Family.Name}' ({symbol.Name}): CTC not set → dialog");

        if (!dialogSvc.ShowFittingCtcSetup(
                symbol.Family.Name, symbol.Name, items, types))
        {
            dialogSvc.ShowWarning("SmartCon",
                "Типы коннекторов не назначены. Фитинг не будет вставлен.");
            return false;
        }

        ApplyFittingCtcToFamily(doc, symbol, items);
        return true;
    }

    public bool EnsureReducerCtcForInsert(
        Document doc, IReadOnlyList<FittingMappingRule> proposedFittings, ConnectorProxy staticConnector)
    {
        if (doc.IsModifiable) return true;

        var reducerRule = proposedFittings
            .FirstOrDefault(r => r.ReducerFamilies.Count > 0);
        if (reducerRule is null) return true;

        var reducerFam = reducerRule.ReducerFamilies[0];
        var symbol = FindFamilySymbol(doc, reducerFam.FamilyName, reducerFam.SymbolName);
        if (symbol is null) return true;

        if (IsFittingCtcDefined(doc, symbol)) return true;

        var types = mappingRepo.GetConnectorTypes();
        if (types.Count == 0) return true;

        bool crossConnect = reducerRule.FromType.Value != reducerRule.ToType.Value;
        var items = BuildConnectorItems(doc, symbol, types, reducerRule, staticConnector.ConnectionTypeCode, crossConnect);

        SmartConLogger.Info($"[CTC] Reducer '{symbol.Family.Name}' ({symbol.Name}): CTC not set → dialog");

        if (!dialogSvc.ShowFittingCtcSetup(
                symbol.Family.Name, symbol.Name, items, types))
        {
            dialogSvc.ShowWarning("SmartCon",
                "Типы коннекторов не назначены. Переходник не будет вставлен.");
            return false;
        }

        ApplyFittingCtcToFamily(doc, symbol, items);
        return true;
    }

    public bool IsFittingCtcDefined(Document doc, FamilySymbol symbol)
    {
        Document? familyDoc = null;
        try
        {
            familyDoc = doc.EditFamily(symbol.Family);
            var connElems = new FilteredElementCollector(familyDoc)
                .OfCategory(BuiltInCategory.OST_ConnectorElem)
                .WhereElementIsNotElementType()
                .Cast<ConnectorElement>()
                .ToList();

            if (connElems.Count < 2) return true;

            foreach (var ce in connElems)
            {
                var desc = ce.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.AsString();
                var ctc = ConnectionTypeCode.Parse(desc);
                if (!ctc.IsDefined) return false;
            }

            return true;
        }
        finally
        {
            familyDoc?.Close(false);
        }
    }

    public List<FittingCtcSetupItem> BuildConnectorItems(
        Document doc, FamilySymbol symbol, IReadOnlyList<ConnectorTypeDefinition> types,
        FittingMappingRule rule, ConnectionTypeCode staticCtc, bool crossConnect = false)
    {
        Document? familyDoc = null;
        try
        {
            familyDoc = doc.EditFamily(symbol.Family);

            var connElems = new FilteredElementCollector(familyDoc)
                .OfCategory(BuiltInCategory.OST_ConnectorElem)
                .WhereElementIsNotElementType()
                .Cast<ConnectorElement>()
                .ToList();

            var items = new List<FittingCtcSetupItem>();

            ConnectionTypeCode? preSelectForConnToStatic = null;
            ConnectionTypeCode? preSelectForConnToDynamic = null;
            if (rule.FromType.Value == staticCtc.Value)
            {
                preSelectForConnToStatic = crossConnect ? rule.ToType : rule.FromType;
                preSelectForConnToDynamic = crossConnect ? rule.FromType : rule.ToType;
            }
            else if (rule.ToType.Value == staticCtc.Value)
            {
                preSelectForConnToStatic = crossConnect ? rule.FromType : rule.ToType;
                preSelectForConnToDynamic = crossConnect ? rule.ToType : rule.FromType;
            }

            for (int i = 0; i < connElems.Count; i++)
            {
                var ce = connElems[i];
                var desc = ce.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.AsString();
                var currentCtc = ConnectionTypeCode.Parse(desc);

                string paramName = GetConnectorParamName(ce, familyDoc);
                double diamMm = ce.Radius * 2.0 * FeetToMm;

                ConnectorTypeDefinition? preSelect = null;
                if (preSelectForConnToStatic.HasValue && preSelectForConnToDynamic.HasValue)
                {
                    var code = i == 0 ? preSelectForConnToStatic.Value : preSelectForConnToDynamic.Value;
                    preSelect = types.FirstOrDefault(t => t.Code == code.Value);
                }

                items.Add(new FittingCtcSetupItem
                {
                    ConnectorIndex = i,
                    ParameterName = paramName,
                    DiameterMm = diamMm,
                    SelectedType = currentCtc.IsDefined
                        ? types.FirstOrDefault(t => t.Code == currentCtc.Value)
                        : null,
                    PreSelectedType = preSelect
                });
            }

            return items;
        }
        finally
        {
            familyDoc?.Close(false);
        }
    }

    public void ApplyFittingCtcToFamily(
        Document doc, FamilySymbol symbol, List<FittingCtcSetupItem> items,
        ElementId? projectElementId = null)
    {
        if (doc.IsModifiable)
        {
            SmartConLogger.Warn($"[CTC] ApplyFittingCtcToFamily: doc.IsModifiable=true, skipping write for '{symbol.Family.Name}'");
            return;
        }

        Document? familyDoc = null;
        try
        {
            familyDoc = doc.EditFamily(symbol.Family);

            var connElems = new FilteredElementCollector(familyDoc)
                .OfCategory(BuiltInCategory.OST_ConnectorElem)
                .WhereElementIsNotElementType()
                .Cast<ConnectorElement>()
                .ToList();

            List<FittingCtcSetupItem> orderedItems = items
                .OrderBy(it => it.ConnectorIndex).ToList();
            Dictionary<int, FittingCtcSetupItem>? spatialMap = null;
            if (projectElementId is not null && connElems.Count >= 2)
                spatialMap = BuildSpatialCtcMap(doc, projectElementId, items, connElems);

            bool anyWritten = false;

            {
                using var familyTx = new Transaction(familyDoc, "SetFittingCtcDescriptions");
                familyTx.Start();

                if (spatialMap is not null)
                {
                    for (int i = 0; i < connElems.Count; i++)
                    {
                        if (!spatialMap.TryGetValue(i, out var item) || item.SelectedType is null) continue;

                        var ce = connElems[i];
                        var typeDef = item.SelectedType!;
                        var value = $"{typeDef.Code}.{typeDef.Name}.{typeDef.Description}";

                        var descParam = ce.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION);

                        if (descParam is not null && !descParam.IsReadOnly)
                        {
                            anyWritten |= descParam.Set(value);
                        }
                        else if (descParam is not null)
                        {
                            anyWritten |= SetDrivingFamilyParameter(
                                familyDoc.FamilyManager, ce, descParam, value);
                        }
                    }
                }
                else
                {
                    var itemByConnIdx = items
                        .Where(it => it.ConnectorIndex >= 0)
                        .ToDictionary(it => it.ConnectorIndex);

                    Dictionary<int, FittingCtcSetupItem>? orderMap = null;
                    if (projectElementId is not null)
                    {
                        var projectConns = connSvc.GetAllConnectors(doc, projectElementId);
                        var sortedConnElems = connElems.OrderBy(ce => ce.Id.Value).ToList();
                        var sortedProjectConns = projectConns.OrderBy(pc => pc.ConnectorIndex).ToList();

                        orderMap = new Dictionary<int, FittingCtcSetupItem>();
                        for (int i = 0; i < sortedConnElems.Count && i < sortedProjectConns.Count; i++)
                        {
                            var origIdx = connElems.IndexOf(sortedConnElems[i]);
                            var pConnIdx = sortedProjectConns[i].ConnectorIndex;
                            if (itemByConnIdx.TryGetValue(pConnIdx, out var item))
                            {
                                orderMap[origIdx] = item;
                                SmartConLogger.Info($"[CTC] Order match: connElem[{origIdx}](id={sortedConnElems[i].Id.Value}) ↔ project conn[{pConnIdx}]");
                            }
                        }

                        if (orderMap.Count == 0)
                        {
                            SmartConLogger.Warn($"[CTC] Order matching: 0 matches — positional fallback");
                            orderMap = null;
                        }
                    }

                    var mapToUse = orderMap;
                    if (mapToUse is not null)
                    {
                        for (int i = 0; i < connElems.Count; i++)
                        {
                            if (!mapToUse.TryGetValue(i, out var item) || item.SelectedType is null) continue;

                            var ce = connElems[i];
                            var typeDef = item.SelectedType!;
                            var value = $"{typeDef.Code}.{typeDef.Name}.{typeDef.Description}";

                            var descParam = ce.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION);

                            if (descParam is not null && !descParam.IsReadOnly)
                            {
                                anyWritten |= descParam.Set(value);
                            }
                            else if (descParam is not null)
                            {
                                anyWritten |= SetDrivingFamilyParameter(
                                    familyDoc.FamilyManager, ce, descParam, value);
                            }
                        }
                    }
                    else
                    {
                        for (int i = 0; i < orderedItems.Count && i < connElems.Count; i++)
                        {
                            if (orderedItems[i].SelectedType is null) continue;

                            var ce = connElems[i];
                            var typeDef = orderedItems[i].SelectedType!;
                            var value = $"{typeDef.Code}.{typeDef.Name}.{typeDef.Description}";

                            var descParam = ce.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION);

                            if (descParam is not null && !descParam.IsReadOnly)
                            {
                                anyWritten |= descParam.Set(value);
                            }
                            else if (descParam is not null)
                            {
                                anyWritten |= SetDrivingFamilyParameter(
                                    familyDoc.FamilyManager, ce, descParam, value);
                            }
                        }
                    }
                }

                if (anyWritten)
                    familyTx.Commit();
            }

            if (anyWritten)
            {
                familyDoc.LoadFamily(doc, new FamilyLoadOptions());
                SmartConLogger.Info($"[CTC] CTC written for '{symbol.Family.Name}'");
            }
        }
        finally
        {
            familyDoc?.Close(false);
        }
    }

    public Dictionary<int, FittingCtcSetupItem>? BuildSpatialCtcMap(
        Document doc,
        ElementId projectElementId,
        List<FittingCtcSetupItem> items,
        List<ConnectorElement> connElems)
    {
        var instance = doc.GetElement(projectElementId) as FamilyInstance;
        if (instance is null) return null;

        var transform = instance.GetTotalTransform();
        var projectConns = connSvc.GetAllConnectors(doc, projectElementId);

        var itemByConnIdx = items
            .Where(it => it.ConnectorIndex >= 0)
            .ToDictionary(it => it.ConnectorIndex);

        var result = new Dictionary<int, FittingCtcSetupItem>();
        var usedItems = new HashSet<int>();

        for (int i = 0; i < connElems.Count; i++)
        {
            var ce = connElems[i];
            var globalOrigin = transform.OfPoint(ce.Origin);

            ConnectorProxy? nearest = null;
            double minDist = double.MaxValue;
            foreach (var pc in projectConns)
            {
                var d = pc.Origin.DistanceTo(globalOrigin);
                if (d < minDist)
                {
                    minDist = d;
                    nearest = pc;
                }
            }

            if (nearest is not null
                && itemByConnIdx.TryGetValue(nearest.ConnectorIndex, out var item)
                && usedItems.Add(nearest.ConnectorIndex))
            {
                result[i] = item;
                SmartConLogger.Info($"[CTC] Spatial match: connElem[{i}](id={ce.Id.Value}) ↔ project conn[{nearest.ConnectorIndex}] (dist={minDist * FeetToMm:F2}mm)");
            }
        }

        if (result.Count != items.Count)
        {
            SmartConLogger.Warn($"[CTC] Spatial matching: matched {result.Count}/{items.Count} items — fallback to positional");
            return null;
        }

        return result;
    }

    public void FlushVirtualCtcToFamilies(
        Document doc, ITransactionGroupSession? groupSession)
    {
        var pendingWrites = virtualCtcStore.GetPendingWrites();
        if (pendingWrites.Count == 0) return;

        var byElement = pendingWrites
            .GroupBy(w => w.ElementId.Value)
            .ToList();

        foreach (var group in byElement)
        {
            var elemId = group.First().ElementId;
            var elem = doc.GetElement(elemId);
            if (elem is null) continue;

            if (elem is FamilyInstance fi)
            {
                var symbol = fi.Symbol;
                var items = group.Select(w => new FittingCtcSetupItem
                {
                    ConnectorIndex = w.ConnectorIndex,
                    ParameterName = string.Empty,
                    DiameterMm = 0,
                    SelectedType = w.TypeDef
                }).ToList();

                ApplyFittingCtcToFamily(doc, symbol, items, projectElementId: elemId);
            }
            else if (elem is MEPCurve or FlexPipe)
            {
                groupSession?.RunInTransaction("PipeConnect — SetConnectorType", d =>
                {
                    foreach (var w in group)
                        familyConnSvc.SetConnectorTypeCode(d, w.ElementId, w.ConnectorIndex, w.TypeDef);
                });
            }
        }

        SmartConLogger.Info($"[CTC] FlushVirtualCtcToFamilies: written {pendingWrites.Count} CTCs for {byElement.Count} elements");

        virtualCtcStore.ClearPendingWrites();
    }

    public static FamilySymbol? FindFamilySymbol(Document doc, string familyName, string symbolName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .FirstOrDefault(s =>
                string.Equals(s.Family.Name, familyName, StringComparison.OrdinalIgnoreCase) &&
                (symbolName == "*" || string.Equals(s.Name, symbolName, StringComparison.OrdinalIgnoreCase)));
    }

    private static string GetConnectorParamName(ConnectorElement ce, Document familyDoc)
    {
        var radiusParam = ce.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS);
        var diamParam = ce.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER);

        foreach (FamilyParameter fp in familyDoc.FamilyManager.GetParameters())
        {
            if (fp.AssociatedParameters.Size == 0) continue;
            try
            {
                foreach (Parameter assoc in fp.AssociatedParameters)
                {
                    bool idMatch = (radiusParam is not null && assoc.Id == radiusParam.Id)
                                || (diamParam is not null && assoc.Id == diamParam.Id);
                    bool elemMatch = assoc.Element?.Id == ce.Id;

                    if (idMatch && elemMatch)
                        return fp.Definition?.Name ?? string.Empty;
                }
            }
            catch { }
        }

        return string.Empty;
    }

    private static bool SetDrivingFamilyParameter(
        FamilyManager fm, ConnectorElement connElem, Parameter connParam, string value)
    {
        FamilyParameter? drivingFp = null;

        foreach (FamilyParameter fp in fm.Parameters)
        {
            try
            {
                foreach (Parameter assoc in fp.AssociatedParameters)
                {
                    if (assoc.Id == connParam.Id && assoc.Element?.Id == connElem.Id)
                    {
                        drivingFp = fp;
                        break;
                    }
                }
            }
            catch { }
            if (drivingFp is not null) break;
        }

        if (drivingFp is null) return false;
        if (!string.IsNullOrEmpty(drivingFp.Formula)) return false;

        try
        {
            if (!drivingFp.IsInstance)
            {
                foreach (FamilyType ft in fm.Types)
                {
                    fm.CurrentType = ft;
                    fm.Set(drivingFp, value);
                }
            }
            else
            {
                fm.Set(drivingFp, value);
            }
            return true;
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"[SetDrivingFamilyParameter] Error (ignored): {ex.Message}");
            return false;
        }
    }

    private sealed class FamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(
            Autodesk.Revit.DB.Family sharedFamily, bool familyInUse,
            out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}

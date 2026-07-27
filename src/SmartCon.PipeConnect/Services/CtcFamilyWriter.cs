using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Compatibility;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// Writes CTC values to family connector descriptions via EditFamily + LoadFamily.
/// Connector matching is index/primary-based (no geometry — issue #161).
/// </summary>
public sealed class CtcFamilyWriter(
    IConnectorService connSvc,
    IFamilyConnectorService familyConnSvc,
    VirtualCtcStore virtualCtcStore)
{
    /// <summary>Find a FamilySymbol by family name and symbol name (case-insensitive, "*" = any).</summary>
    public static FamilySymbol? FindFamilySymbol(Document doc, string familyName, string symbolName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .FirstOrDefault(s =>
                string.Equals(s.Family.Name, familyName, StringComparison.OrdinalIgnoreCase) &&
                (symbolName == "*" || string.Equals(s.Name, symbolName, StringComparison.OrdinalIgnoreCase)));
    }

    public void ApplyFittingCtcToFamily(
        Document doc, FamilySymbol symbol, List<FittingCtcSetupItem> items,
        ElementId? projectElementId = null)
    {
        using var _scope = SmartConLogger.BeginScope("CTC",
            ("Method", "ApplyFittingCtcToFamily"),
            ("FamilyName", symbol.Family.Name));

        if (doc.IsModifiable)
        {
            SmartConLogger.Warn($"doc.IsModifiable=true, skipping write for '{symbol.Family.Name}'");
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
            Dictionary<int, FittingCtcSetupItem>? connectorMap = null;
            if (projectElementId is not null && connElems.Count >= 2)
                connectorMap = BuildConnectorCtcMap(doc, projectElementId, items, connElems);

            bool anyWritten = false;

            {
                using var familyTx = new Transaction(familyDoc, "SetFittingCtcDescriptions");
                familyTx.Start();

                if (connectorMap is not null)
                {
                    for (int i = 0; i < connElems.Count; i++)
                    {
                        if (!connectorMap.TryGetValue(i, out var item) || item.SelectedType is null) continue;

                        var ce = connElems[i];
                        anyWritten |= WriteCtcToConnector(familyDoc, ce, item.SelectedType!);
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
                        var sortedConnElems = connElems.OrderBy(ce => ce.Id.GetValue()).ToList();
                        var sortedProjectConns = projectConns.OrderBy(pc => pc.ConnectorIndex).ToList();

                        orderMap = new Dictionary<int, FittingCtcSetupItem>();
                        for (int i = 0; i < sortedConnElems.Count && i < sortedProjectConns.Count; i++)
                        {
                            var origIdx = connElems.IndexOf(sortedConnElems[i]);
                            var pConnIdx = sortedProjectConns[i].ConnectorIndex;
                            if (itemByConnIdx.TryGetValue(pConnIdx, out var item))
                            {
                                orderMap[origIdx] = item;
                                SmartConLogger.Info($"Order match: connElem[{origIdx}](id={sortedConnElems[i].Id.GetValue()}) ↔ project conn[{pConnIdx}]");
                            }
                        }

                        if (orderMap.Count == 0)
                        {
                            SmartConLogger.Warn("Order matching: 0 matches — positional fallback");
                            orderMap = null;
                        }
                    }

                    var mapToUse = orderMap;
                    if (mapToUse is not null)
                    {
                        for (int i = 0; i < connElems.Count; i++)
                        {
                            if (!mapToUse.TryGetValue(i, out var item) || item.SelectedType is null) continue;
                            anyWritten |= WriteCtcToConnector(familyDoc, connElems[i], item.SelectedType!);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < orderedItems.Count && i < connElems.Count; i++)
                        {
                            if (orderedItems[i].SelectedType is null) continue;
                            anyWritten |= WriteCtcToConnector(familyDoc, connElems[i], orderedItems[i].SelectedType!);
                        }
                    }
                }

                if (anyWritten)
                    familyTx.Commit();
            }

            if (anyWritten)
            {
                familyDoc.LoadFamily(doc, new FamilyLoadOptions());
                SmartConLogger.Info($"CTC written for '{symbol.Family.Name}'");
            }
        }
        finally
        {
            familyDoc?.Close(false);
        }
    }

    /// <summary>
    /// Map family-doc ConnectorElements to project connectors WITHOUT geometry:
    /// the origin-based spatial match broke whenever instance parameters (elbow
    /// angle, DN) differed from the family template — a 45°-template elbow at
    /// 90° wrote CTC to the wrong connector (issue #161).
    /// Cascade:
    /// 1. IsPrimary — the single primary connector of the family discipline
    ///    (aectechtalk: one primary per discipline) anchors one pair; exact for
    ///    2-connector fittings (elbows, valves, nipples, reducers).
    /// 2. Index order — the connector index is serialized on ConnectorElement
    ///    and only grows with creation order (Tammik, mep_connector_number), and
    ///    ConnectorElement ElementIds are assigned in the same creation order,
    ///    so sorting both sides aligns the same physical connectors.
    /// Returns null (positional fallback) unless every item is matched.
    /// </summary>
    public Dictionary<int, FittingCtcSetupItem>? BuildConnectorCtcMap(
        Document doc,
        ElementId projectElementId,
        List<FittingCtcSetupItem> items,
        List<ConnectorElement> connElems)
    {
        var instance = doc.GetElement(projectElementId) as FamilyInstance;
        if (instance is null) return null;

        var cm = instance.MEPModel?.ConnectorManager;
        if (cm is null) return null;

        var projectConns = new List<Connector>();
        foreach (Connector c in cm.Connectors)
        {
            if (c.ConnectorType == ConnectorType.Curve) continue;
            if (c.Domain != Domain.DomainPiping) continue;
            projectConns.Add(c);
        }

        var itemByConnIdx = items
            .Where(it => it.ConnectorIndex >= 0)
            .ToDictionary(it => it.ConnectorIndex);

        var result = new Dictionary<int, FittingCtcSetupItem>();
        var usedConnIdx = new HashSet<int>();
        var usedCeIdx = new HashSet<int>();

        // 1. IsPrimary anchor
        int primaryCeIdx = -1;
        for (int i = 0; i < connElems.Count; i++)
        {
            if (connElems[i].IsPrimary) { primaryCeIdx = i; break; }
        }
        if (primaryCeIdx >= 0)
        {
            foreach (var pc in projectConns)
            {
                var info = pc.GetMEPConnectorInfo();
                if (info is null || !info.IsPrimary) continue;
                int pcIdx = (int)pc.Id;
                if (itemByConnIdx.TryGetValue(pcIdx, out var item) && usedConnIdx.Add(pcIdx))
                {
                    result[primaryCeIdx] = item;
                    usedCeIdx.Add(primaryCeIdx);
                    SmartConLogger.Info($"Primary match: connElem[{primaryCeIdx}](id={connElems[primaryCeIdx].Id.GetValue()}) ↔ project conn[{pcIdx}]");
                }
                break;
            }
        }

        // 2. Index-order match for the remainder
        var remainingCes = connElems
            .Select((ce, idx) => (ce, idx))
            .Where(t => !usedCeIdx.Contains(t.idx))
            .OrderBy(t => t.ce.Id.GetValue())
            .ToList();
        var remainingPcs = projectConns
            .Select(pc => (int)pc.Id)
            .Where(idx => !usedConnIdx.Contains(idx) && itemByConnIdx.ContainsKey(idx))
            .OrderBy(idx => idx)
            .ToList();

        for (int i = 0; i < remainingCes.Count && i < remainingPcs.Count; i++)
        {
            var (ce, ceIdx) = remainingCes[i];
            int pcIdx = remainingPcs[i];
            result[ceIdx] = itemByConnIdx[pcIdx];
            usedConnIdx.Add(pcIdx);
            SmartConLogger.Info($"Index-order match: connElem[{ceIdx}](id={ce.Id.GetValue()}) ↔ project conn[{pcIdx}]");
        }

        if (result.Count != items.Count)
        {
            SmartConLogger.Warn($"Connector matching: matched {result.Count}/{items.Count} items — fallback to positional [Action: проверьте CTC-маппинг коннекторов вручную после записи]");
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
            .GroupBy(w => w.ElementId.GetValue())
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
                groupSession?.RunInTransaction(LocalizationService.GetString("Tx_SetCtc"), d =>
                {
                    foreach (var w in group)
                        familyConnSvc.SetConnectorTypeCode(d, w.ElementId, w.ConnectorIndex, w.TypeDef);
                });
            }
        }

        SmartConLogger.Info($"FlushVirtualCtcToFamilies: written {pendingWrites.Count} CTCs for {byElement.Count} elements");

        virtualCtcStore.ClearPendingWrites();
    }

    private bool WriteCtcToConnector(Document familyDoc, ConnectorElement ce, ConnectorTypeDefinition typeDef)
    {
        var value = $"{typeDef.Code}.{typeDef.Name}.{typeDef.Description}";
        var descParam = ce.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION);

        if (descParam is not null && !descParam.IsReadOnly)
            return descParam.Set(value);

        if (descParam is not null)
            return SetDrivingFamilyParameter(familyDoc.FamilyManager, ce, descParam, value);

        return false;
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
            catch
            {
            }
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
            SmartConLogger.Info($"SetDrivingFamilyParameter error (ignored): {ex.Message}");
            return false;
        }
    }

    private sealed class FamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            SmartConLogger.Info($"CTC LoadFamily.OnFamilyFound: familyInUse={familyInUse}, keep user params");
            overwriteParameterValues = false;
            return true;
        }

        public bool OnSharedFamilyFound(
            Autodesk.Revit.DB.Family sharedFamily, bool familyInUse,
            out FamilySource source, out bool overwriteParameterValues)
        {
            var familyName = sharedFamily?.Name ?? "<null>";
            SmartConLogger.Info($"CTC LoadFamily.OnSharedFamilyFound: '{familyName}', familyInUse={familyInUse}, keep project version");
            source = FamilySource.Project;
            overwriteParameterValues = false;
            return true;
        }
    }
}

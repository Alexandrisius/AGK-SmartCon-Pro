using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Compatibility;
using SmartCon.PipeConnect.ViewModels;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// Writes CTC values to family connector descriptions via EditFamily + LoadFamily.
/// Handles spatial matching of connector elements to project connectors.
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
                                SmartConLogger.Info($"[CTC] Order match: connElem[{origIdx}](id={sortedConnElems[i].Id.GetValue()}) ↔ project conn[{pConnIdx}]");
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
                SmartConLogger.Info($"[CTC] Spatial match: connElem[{i}](id={ce.Id.GetValue()}) ↔ project conn[{nearest.ConnectorIndex}] (dist={minDist * FeetToMm:F2}mm)");
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

        SmartConLogger.Info($"[CTC] FlushVirtualCtcToFamilies: written {pendingWrites.Count} CTCs for {byElement.Count} elements");

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

    public static string GetConnectorParamName(ConnectorElement ce, Document familyDoc)
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
            catch
            {
            }
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

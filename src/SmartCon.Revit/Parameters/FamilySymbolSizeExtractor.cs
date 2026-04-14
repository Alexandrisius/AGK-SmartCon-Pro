using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Extensions;
using static SmartCon.Core.Units;

namespace SmartCon.Revit.Parameters;

public sealed class FamilySymbolSizeExtractor
{
    private readonly ITransactionService _transactionService;

    public FamilySymbolSizeExtractor(ITransactionService transactionService)
    {
        _transactionService = transactionService;
    }

    public SortedSet<double> GetSymbolRadii(Document doc, ElementId instanceId, int connectorIndex)
    {
        SmartConLogger.DebugSection("FamilySymbolSizeExtractor.GetSymbolRadii");

        var instance = doc.GetElement(instanceId) as FamilyInstance;
        if (instance is null) return [];

        var family = instance.Symbol?.Family;
        if (family is null) return [];

        var symbolIds = family.GetFamilySymbolIds().ToList();
        SmartConLogger.Debug($"  Family '{family.Name}': {symbolIds.Count} symbols");

        var radii = new SortedSet<double>();

        _transactionService.RunAndRollback("SmartCon_TrialSymbolRadii", txDoc =>
        {
            foreach (var symbolId in symbolIds)
            {
                try
                {
                    var inst = txDoc.GetElement(instanceId) as FamilyInstance;
                    if (inst is null) continue;

                    var sym = txDoc.GetElement(symbolId) as FamilySymbol;
                    inst.ChangeTypeId(symbolId);
                    txDoc.Regenerate();

                    var cm = inst.MEPModel?.ConnectorManager;
                    var conn = cm?.FindByIndex(connectorIndex);
                    if (conn is not null)
                    {
                        radii.Add(conn.Radius);
                        SmartConLogger.Debug($"  symbol '{sym?.Name}': radius={conn.Radius * FeetToMm:F2} mm");
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug($"  EXCEPTION symbolId={symbolId.Value}: {ex.Message}");
                }
            }
        });

        return radii;
    }

    public List<(ElementId SymbolId, Dictionary<int, double> ConnectorRadii, string SymbolName)> GetSymbolConnectorRadii(
        Document doc, ElementId instanceId, int targetConnectorIndex)
    {
        SmartConLogger.DebugSection("FamilySymbolSizeExtractor.GetSymbolConnectorRadii");

        var instance = doc.GetElement(instanceId) as FamilyInstance;
        if (instance is null) return [];

        var family = instance.Symbol?.Family;
        if (family is null) return [];

        var symbolIds = family.GetFamilySymbolIds().ToList();
        SmartConLogger.Debug($"  Family '{family.Name}': {symbolIds.Count} symbols");

        var symbolData = new List<(ElementId SymbolId, Dictionary<int, double> ConnectorRadii, string SymbolName)>();

        _transactionService.RunAndRollback("SmartCon_TrialSymbolConfigs", txDoc =>
        {
            foreach (var symbolId in symbolIds)
            {
                try
                {
                    var inst = txDoc.GetElement(instanceId) as FamilyInstance;
                    if (inst is null) continue;

                    var sym = txDoc.GetElement(symbolId) as FamilySymbol;
                    inst.ChangeTypeId(symbolId);
                    txDoc.Regenerate();

                    var cm = inst.MEPModel?.ConnectorManager;
                    if (cm is null) continue;

                    var connectorRadii = new Dictionary<int, double>();
                    foreach (Connector c in cm.Connectors)
                    {
                        if (c.ConnectorType == ConnectorType.Curve) continue;
                        connectorRadii[(int)c.Id] = c.Radius;
                    }

                    var targetRadius = connectorRadii.GetValueOrDefault(targetConnectorIndex, 0);
                    if (targetRadius <= 0) continue;

                    symbolData.Add((symbolId, connectorRadii, sym?.Name ?? ""));
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug($"  error symbolId={symbolId.Value}: {ex.Message}");
                }
            }
        });

        return symbolData;
    }

    public static List<List<int>> AnalyzeSharedParameterGroups(
        List<(ElementId SymbolId, Dictionary<int, double> ConnectorRadii, string SymbolName)> symbolData)
    {
        if (symbolData.Count == 0) return [];

        var allIds = symbolData[0].ConnectorRadii.Keys.OrderBy(id => id).ToList();
        var groups = new List<List<int>>();
        var assigned = new HashSet<int>();

        foreach (var id in allIds)
        {
            if (assigned.Contains(id)) continue;

            var group = new List<int> { id };
            assigned.Add(id);

            foreach (var otherId in allIds)
            {
                if (assigned.Contains(otherId)) continue;

                bool alwaysSame = symbolData.All(sd =>
                {
                    var r1 = sd.ConnectorRadii.GetValueOrDefault(id, -1);
                    var r2 = sd.ConnectorRadii.GetValueOrDefault(otherId, -1);
                    return System.Math.Abs(r1 - r2) < 1e-9;
                });

                if (alwaysSame)
                {
                    group.Add(otherId);
                    assigned.Add(otherId);
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    public static Dictionary<int, double> BuildDisplayRadii(
        Dictionary<int, double> connectorRadii,
        List<List<int>> sharedParamGroups,
        int targetConnectorIndex)
    {
        var result = new Dictionary<int, double>();

        foreach (var group in sharedParamGroups)
        {
            var repId = group.Contains(targetConnectorIndex)
                ? targetConnectorIndex
                : group[0];

            if (connectorRadii.TryGetValue(repId, out var radius))
                result[repId] = radius;
        }

        return result;
    }
}

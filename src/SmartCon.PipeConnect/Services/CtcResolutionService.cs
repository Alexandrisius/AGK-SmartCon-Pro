using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// Resolves CTC (ConnectionTypeCode) for connector sides: which connector faces
/// static vs dynamic, effective CTC with virtual overrides, and rule-based type resolution.
/// </summary>
public sealed class CtcResolutionService(
    IConnectorService connSvc,
    IFittingMappingRepository mappingRepo,
    VirtualCtcStore virtualCtcStore)
{
    /// <summary>
    /// Determine the dynamic-side CTC from a fitting mapping rule and the static CTC.
    /// </summary>
    public ConnectionTypeCode ResolveDynamicTypeFromRule(
        FittingMappingRule? rule, ConnectionTypeCode staticCtc)
    {
        if (rule is null) return default;
        if (rule.FromType.Value == staticCtc.Value)
            return rule.ToType;
        return rule.FromType;
    }

    /// <summary>
    /// Refresh a connector proxy and apply virtual CTC override if present.
    /// </summary>
    public ConnectorProxy? RefreshWithCtcOverride(
        Document doc, ElementId elemId, int connIdx)
    {
        var proxy = connSvc.RefreshConnector(doc, elemId, connIdx);
        if (proxy is null) return null;
        var ctc = virtualCtcStore.Get(elemId, connIdx);
        return ctc.HasValue ? proxy with { ConnectionTypeCode = ctc.Value } : proxy;
    }

    /// <summary>Get the effective CTC for a connector: virtual override takes priority over actual.</summary>
    public ConnectionTypeCode GetEffectiveConnectorCtc(ElementId elementId, ConnectorProxy conn)
    {
        var vCtc = virtualCtcStore.Get(elementId, conn.ConnectorIndex);
        return vCtc ?? conn.ConnectionTypeCode;
    }

    /// <summary>
    /// Resolve which connector of an element faces the static side and which faces dynamic.
    /// Uses CTC matching rules first, falls back to spatial proximity.
    /// </summary>
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
}

using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// Tracks visited connectors and provides sequential cycling through free connectors on an element.
/// </summary>
public sealed class ConnectorCycleState
{
    private List<ConnectorProxy> _allConnectors = [];
    private readonly HashSet<int> _visited = [];
    private int _position;

    public IReadOnlyList<ConnectorProxy> Connectors => _allConnectors;
    public int Count => _allConnectors.Count;

    public void Initialize(List<ConnectorProxy> connectors, ConnectorProxy active)
    {
        _allConnectors = connectors;
        _visited.Clear();
        _position = 0;

        _visited.Add(active.ConnectorIndex);
        int idx = connectors.FindIndex(c => c.ConnectorIndex == active.ConnectorIndex);
        _position = (Math.Max(idx, 0) + 1) % Math.Max(1, connectors.Count);
    }

    public ConnectorProxy? FindNext()
    {
        int count = _allConnectors.Count;
        if (count == 0) return null;

        for (int i = 0; i < count; i++)
        {
            int idx = (_position + i) % count;
            var conn = _allConnectors[idx];
            if (!_visited.Contains(conn.ConnectorIndex))
            {
                _position = (idx + 1) % count;
                return conn;
            }
        }

        _visited.Clear();
        var first = _allConnectors[_position % count];
        _position = (_position + 1) % count;
        return first;
    }

    public void MarkVisited(int connectorIndex) => _visited.Add(connectorIndex);
}

/// <summary>
/// Handles cycling through free connectors on an element and re-aligning after each switch.
/// </summary>
public sealed class ConnectorCycleService(
    IConnectorService connSvc,
    ITransformService transformSvc,
    IParameterResolver paramResolver,
    FittingCtcManager ctcManager)
{
    public ConnectorCycleState State { get; } = new();

    public ConnectorProxy? CycleAndAlign(
        Document doc,
        ITransactionGroupSession session,
        ConnectorProxy target,
        ConnectorProxy alignTarget,
        ConnectorProxy? currentActive)
    {
        paramResolver.GetConnectorRadiusDependencies(doc, target.OwnerElementId, target.ConnectorIndex);

        ConnectorProxy? result = currentActive;

        session.RunInTransaction(LocalizationService.GetString("Tx_SwitchConnector"), d =>
        {
            var freshTarget = connSvc.RefreshConnector(
                d, target.OwnerElementId, target.ConnectorIndex) ?? target;

            var reAlign = ConnectorAligner.ComputeAlignment(
                alignTarget.OriginVec3, alignTarget.BasisZVec3, alignTarget.BasisXVec3,
                freshTarget.OriginVec3, freshTarget.BasisZVec3, freshTarget.BasisXVec3);

            var dynId = target.OwnerElementId;
            if (!VectorUtils.IsZero(reAlign.InitialOffset))
                transformSvc.MoveElement(d, dynId, reAlign.InitialOffset);
            if (reAlign.BasisZRotation is { } bz)
                transformSvc.RotateElement(d, dynId, reAlign.RotationCenter, bz.Axis, bz.AngleRadians);
            if (reAlign.BasisXSnap is { } bx)
                transformSvc.RotateElement(d, dynId, reAlign.RotationCenter, bx.Axis, bx.AngleRadians);

            d.Regenerate();
            var r = connSvc.RefreshConnector(d, dynId, target.ConnectorIndex);
            if (r is not null)
            {
                var corr = alignTarget.OriginVec3 - r.OriginVec3;
                if (!VectorUtils.IsZero(corr))
                    transformSvc.MoveElement(d, dynId, corr);
            }
            d.Regenerate();
            result = ctcManager.RefreshWithCtcOverride(d, dynId, target.ConnectorIndex);
        });

        State.MarkVisited(target.ConnectorIndex);
        return result;
    }
}

using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// Manages cyclic iteration through connectors of a multi-port element.
/// Tracks visited connectors and wraps around when all have been visited.
/// </summary>
public sealed class ConnectorCycleState
{
    private List<ConnectorProxy> _allConnectors = [];
    private readonly HashSet<int> _visited = [];
    private int _position;

    /// <summary>All connectors in the cycle.</summary>
    public IReadOnlyList<ConnectorProxy> Connectors => _allConnectors;

    /// <summary>Total connector count.</summary>
    public int Count => _allConnectors.Count;

    /// <summary>Initialize the cycle with connectors and mark the active one as visited.</summary>
    /// <param name="connectors">All connectors of the element.</param>
    /// <param name="active">Currently active connector (starting point).</param>
    public void Initialize(List<ConnectorProxy> connectors, ConnectorProxy active)
    {
        _allConnectors = connectors;
        _visited.Clear();
        _position = 0;

        _visited.Add(active.ConnectorIndex);
        int idx = connectors.FindIndex(c => c.ConnectorIndex == active.ConnectorIndex);
        _position = (Math.Max(idx, 0) + 1) % Math.Max(1, connectors.Count);
    }

    /// <summary>
    /// Find the next unvisited connector in the cycle. Wraps around and clears
    /// the visited set when all connectors have been visited.
    /// </summary>
    /// <returns>Next connector, or null if no connectors exist.</returns>
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

    /// <summary>Mark a connector as visited.</summary>
    public void MarkVisited(int connectorIndex) => _visited.Add(connectorIndex);

    /// <summary>Remove a connector from the visited set.</summary>
    public void UnmarkVisited(int connectorIndex) => _visited.Remove(connectorIndex);
}

/// <summary>
/// Handles connector cycling on multi-port elements: re-aligns the element
/// when the user switches to a different connector and refreshes CTC overrides.
/// </summary>
public sealed class ConnectorCycleService(
    IConnectorService connSvc,
    IAlignmentService alignmentSvc,
    IParameterResolver paramResolver,
    FittingCtcManager ctcManager)
{
    /// <summary>Cycle state tracking visited connectors.</summary>
    public ConnectorCycleState State { get; } = new();

    /// <summary>
    /// Switch to the target connector, re-align the element, and refresh CTC overrides.
    /// </summary>
    /// <param name="doc">Active Revit document.</param>
    /// <param name="session">Active transaction group session.</param>
    /// <param name="target">Connector to switch to.</param>
    /// <param name="alignTarget">Connector to align against.</param>
    /// <param name="currentActive">Currently active connector (for reference).</param>
    /// <returns>Refreshed connector proxy with updated CTC, or null on failure.</returns>
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

            alignmentSvc.ApplyAlignment(d, target.OwnerElementId, alignTarget, freshTarget);

            result = ctcManager.RefreshWithCtcOverride(d, target.OwnerElementId, target.ConnectorIndex);
        });

        State.MarkVisited(target.ConnectorIndex);
        return result;
    }
}

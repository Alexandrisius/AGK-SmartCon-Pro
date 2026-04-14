using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Operations on element connectors: getting proxy, finding nearest, ConnectTo.
/// Implementation: SmartCon.Revit/Selection/ConnectorService.cs
/// </summary>
public interface IConnectorService
{
    /// <summary>
    /// Get ConnectorProxy of the nearest free connector to the click point.
    /// Returns null if no free connectors.
    /// </summary>
    ConnectorProxy? GetNearestFreeConnector(Document doc, ElementId elementId, XYZ clickPoint);

    /// <summary>
    /// Re-read the current connector state (after transformation).
    /// </summary>
    ConnectorProxy? RefreshConnector(Document doc, ElementId elementId, int connectorIndex);

    /// <summary>
    /// Connect two connectors via Connector.ConnectTo().
    /// Returns true on success.
    /// </summary>
    bool ConnectTo(Document doc,
        ElementId elementId1, int connectorIndex1,
        ElementId elementId2, int connectorIndex2);

    /// <summary>
    /// Disconnect the specified connector from all existing connections.
    /// Used to isolate the dynamic element before moving.
    /// </summary>
    void DisconnectAllFromConnector(Document doc, ElementId elementId, int connectorIndex);

    /// <summary>
    /// Get all free connectors of an element (excluding ConnectorType.Curve).
    /// Used for ComboBox connector selection in PipeConnectEditor (Phase 8).
    /// </summary>
    IReadOnlyList<ConnectorProxy> GetAllFreeConnectors(Document doc, ElementId elementId);

    /// <summary>
    /// Get ALL connectors of an element (free AND connected), excluding ConnectorType.Curve.
    /// Used for pre-disconnecting the dynamic element.
    /// </summary>
    IReadOnlyList<ConnectorProxy> GetAllConnectors(Document doc, ElementId elementId);
}

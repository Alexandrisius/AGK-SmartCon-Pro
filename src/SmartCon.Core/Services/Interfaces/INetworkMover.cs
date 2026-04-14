using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Inserts a reducer between two connectors with different DN.
/// Used in IncrementChainDepth when AdjustSize failed to fit the element.
/// </summary>
public interface INetworkMover
{
    /// <summary>
    /// Insert a reducer between parentConn and childConn.
    /// Reducer is aligned to parentConn. Returns ElementId of the inserted reducer or null.
    /// </summary>
    ElementId? InsertReducer(Document doc,
        ConnectorProxy parentConn, ConnectorProxy childConn,
        IReadOnlyDictionary<int, ConnectionTypeCode>? ctcOverrides = null,
        IReadOnlyList<FittingMappingRule>? directConnectRules = null);
}

using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Works with family lookup tables (FamilySizeTableManager).
/// Implementation: SmartCon.Revit/Parameters/RevitLookupTableService.cs
/// Call OUTSIDE transaction (EditFamily requires doc.IsModifiable == false).
/// </summary>
public interface ILookupTableService
{
    /// <summary>
    /// Does the lookup table have a row with the given connector radius?
    /// Returns false if the element has no size_lookup table.
    /// constraints — restrictions by other DN columns (for multi-query fittings).
    /// </summary>
    bool ConnectorRadiusExistsInTable(Document doc, ElementId elementId,
        int connectorIndex, double radiusInternalUnits,
        IReadOnlyList<LookupColumnConstraint>? constraints = null);

    /// <summary>
    /// Nearest available radius in the lookup table.
    /// Returns targetRadiusInternalUnits if no table exists (fallback).
    /// constraints — restrictions by other DN columns (for multi-query fittings).
    /// </summary>
    double GetNearestAvailableRadius(Document doc, ElementId elementId,
        int connectorIndex, double targetRadiusInternalUnits,
        IReadOnlyList<LookupColumnConstraint>? constraints = null);

    /// <summary>
    /// Does the element have a size_lookup table controlling the connector radius?
    /// </summary>
    bool HasLookupTable(Document doc, ElementId elementId, int connectorIndex);

    /// <summary>
    /// Get ALL rows of the lookup table as full configurations.
    /// Each row contains radii of ALL connectors (by column-to-connector mapping).
    /// If constraints are specified — filters rows like GetAvailableSizes.
    /// If constraints = null — returns all rows without filtering.
    /// Call OUTSIDE transaction.
    /// </summary>
    IReadOnlyList<SizeTableRow> GetAllSizeRows(Document doc, ElementId elementId,
        int targetConnectorIndex,
        IReadOnlyList<LookupColumnConstraint>? constraints = null);
}

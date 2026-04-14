using Autodesk.Revit.DB;
using SmartCon.Core.Math;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Insertion and positioning of fittings via Revit API (Phase 5 + 8).
/// Implementation: SmartCon.Revit/Fittings/RevitFittingInsertService.cs
/// Call only inside Transaction (I-03).
/// </summary>
public interface IFittingInsertService
{
    /// <summary>
    /// Insert a fitting FamilyInstance at the specified point.
    /// Returns ElementId of the inserted instance or null if family/type not found.
    /// </summary>
    ElementId? InsertFitting(Document doc, string familyName, string symbolName, XYZ position);

    /// <summary>
    /// Align the fitting so that the connector matching staticProxy by type (CTC)
    /// coincides with staticProxy in position and orientation.
    /// <paramref name="dynamicTypeCode"/> — connector type of the dynamic side (from FittingMappingRule).
    /// Used to determine orientation: fitting connector with CTC == dynamicTypeCode faces the pipe.
    /// Returns ConnectorProxy of the second fitting connector after alignment (null on error).
    /// </summary>
    ConnectorProxy? AlignFittingToStatic(
        Document doc,
        ElementId fittingId,
        ConnectorProxy staticProxy,
        ITransformService transformSvc,
        IConnectorService connSvc,
        ConnectionTypeCode dynamicTypeCode = default,
        IReadOnlyDictionary<int, ConnectionTypeCode>? ctcOverrides = null,
        IReadOnlyList<FittingMappingRule>? directConnectRules = null);

    /// <summary>
    /// Delete an element from the document.
    /// </summary>
    void DeleteElement(Document doc, ElementId elementId);
}

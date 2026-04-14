using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Writing connection type to connector/type Description via Revit API.
/// Implementation: SmartCon.Revit/Family/RevitFamilyConnectorService.cs
/// </summary>
public interface IFamilyConnectorService
{
    /// <summary>
    /// Write connection type to the Description of a connector or pipe type.
    /// Format: "CODE.NAME.DESCRIPTION" (read via ConnectionTypeCode.Parse).
    /// For pipes — call INSIDE transaction (I-03).
    /// For fittings — call OUTSIDE transaction (EditFamily requires IsModifiable==false).
    /// </summary>
    bool SetConnectorTypeCode(Document doc, ElementId elementId,
                              int connectorIndex, ConnectorTypeDefinition typeDef);
}

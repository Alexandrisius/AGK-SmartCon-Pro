using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;

namespace SmartCon.Revit.Wrappers;

/// <summary>
/// Утилита создания ConnectorProxy из Revit.DB.Connector.
/// Единственная точка маппинга Revit Connector -> доменная модель Core.
/// </summary>
public static class ConnectorWrapper
{
    /// <summary>
    /// Создаёт иммутабельный ConnectorProxy из актуального Revit Connector.
    /// Вызывать внутри транзакции или ExternalEventHandler.Execute().
    /// </summary>
    public static ConnectorProxy ToProxy(Connector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);

        var cs = connector.CoordinateSystem;

        return new ConnectorProxy
        {
            OwnerElementId = connector.Owner.Id,
            ConnectorIndex = (int)connector.Id,
            Origin = cs.Origin,
            BasisZ = cs.BasisZ,
            BasisX = cs.BasisX,
            Radius = connector.Radius,
            Domain = connector.Domain,
            ConnectionTypeCode = ConnectionTypeCode.Parse(GetConnectionDescription(connector)),
            IsFree = !connector.IsConnected
        };
    }

    /// <summary>
    /// Читает описание типа соединения:
    /// — для труб/гибких труб — из параметра «Описание» типоразмера элемента;
    /// — для фитингов — из connector.Description (записывается через EditFamily).
    /// </summary>
    private static string? GetConnectionDescription(Connector connector)
    {
        var owner = connector.Owner;
        if (owner is MEPCurve or FlexPipe)
            return GetTypeDescriptionSafe(owner);
        return GetConnectorDescriptionSafe(connector);
    }

    private static string? GetTypeDescriptionSafe(Element element)
    {
        try
        {
            var typeId = element.GetTypeId();
            var elemType = element.Document.GetElement(typeId);
            // BuiltInParameter.ALL_MODEL_DESCRIPTION — языконезависимый системный параметр «Описание».
            return elemType?.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString();
        }
        catch (Exception ex) { SmartConLogger.Warn($"[ConnectorWrapper] GetTypeDescriptionSafe: {ex.GetType().Name}: {ex.Message}"); return null; }
    }

    private static string? GetConnectorDescriptionSafe(Connector connector)
    {
        try { return connector.Description; }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return null; }
    }
}

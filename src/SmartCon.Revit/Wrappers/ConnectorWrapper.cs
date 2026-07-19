using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Revit.Extensions;

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
#if NETFRAMEWORK
        if (connector is null) throw new ArgumentNullException(nameof(connector));
#else
        ArgumentNullException.ThrowIfNull(connector);
#endif

        var cs = connector.CoordinateSystem;
        var description = GetConnectionDescription(connector);
        var parsed = ConnectorDescription.Parse(description);

        double radius = 0.0;
        if (connector.IsRoundSafe())
        {
            radius = connector.Radius;
        }
        else
        {
            SmartConLogger.Warn($"ToProxy: non-round pipe connector, Radius forced to 0: {DescribeConnector(connector)}. " +
                $"[Action: откройте семейство в редакторе и выставьте коннектору Shape=Round — дефектный коннектор обнаружен, см. ElementId]");
        }

        return new ConnectorProxy
        {
            OwnerElementId = connector.Owner!.Id,
            ConnectorIndex = (int)connector.Id,
            Origin = cs.Origin,
            BasisZ = cs.BasisZ,
            BasisX = cs.BasisX,
            Radius = radius,
            Domain = connector.Domain,
            ConnectionTypeCode = parsed.Code,
            ConnectionName = parsed.Name,
            ConnectionDescription = parsed.Description,
            IsFree = !connector.IsConnected
        };
    }

    /// <summary>
    /// Диагностическое описание коннектора и его владельца (для логов).
    /// Никогда не бросает исключений.
    /// </summary>
    public static string DescribeConnector(Connector connector)
    {
        try
        {
            var owner = connector.Owner;
            if (owner is null)
                return $"connId={connector.Id}, owner=<null>";

            string familyInfo = string.Empty;
            if (owner is FamilyInstance fi)
            {
                string familyName = fi.Symbol?.FamilyName ?? "?";
                string typeName = fi.Symbol?.Name ?? "?";
                familyInfo = $", family='{familyName}', type='{typeName}'";
            }

            string shape;
            try { shape = connector.Shape.ToString(); }
            catch (Exception ex) { shape = $"<unavailable: {ex.GetType().Name}>"; }

            return $"connId={connector.Id}, elementId={owner.Id}, elementName='{owner.Name}', " +
                   $"category='{owner.Category?.Name ?? "?"}'{familyInfo}, " +
                   $"shape={shape}, domain={connector.Domain}, connectorType={connector.ConnectorType}, " +
                   $"isConnected={connector.IsConnected}";
        }
        catch (Exception ex)
        {
            return $"<DescribeConnector failed: {ex.GetType().Name}: {ex.Message}>";
        }
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
        catch (Exception ex)
        {
            SmartConLogger.Warn($"ConnectorWrapper.GetTypeDescriptionSafe: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string? GetConnectorDescriptionSafe(Connector connector)
    {
        try { return connector.Description; }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return null; }
    }
}

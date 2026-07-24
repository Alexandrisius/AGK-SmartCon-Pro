using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;

namespace SmartCon.Revit.Parameters;

/// <summary>
/// Resolves the family parameter driving a connector's size (CONNECTOR_RADIUS
/// or CONNECTOR_DIAMETER) directly from the PROJECT via MEPFamilyConnectorInfo —
/// no EditFamily, no geometry matching. Replaces the legacy origin/direction
/// matching against ConnectorElement that broke whenever instance parameters
/// (elbow angle, DN) differed from the family template (issue #161).
/// </summary>
internal static class ConnectorSizeBindingResolver
{
    /// <summary>
    /// Returns (ParamName, IsDiameter) of the family parameter bound to the
    /// connector's CONNECTOR_RADIUS (preferred) or CONNECTOR_DIAMETER, or null
    /// when the connector has no size binding (or is not a family connector —
    /// e.g. MEPCurve connectors return a different info class).
    /// </summary>
    internal static (string ParamName, bool IsDiameter)? TryGetSizeBinding(
        Document doc, Connector connector)
    {
        var mepInfo = connector.GetMEPConnectorInfo() as MEPFamilyConnectorInfo;
        if (mepInfo is null)
        {
            SmartConLogger.Debug("  GetMEPConnectorInfo()=null (not MEPFamilyConnectorInfo) → no size binding");
            return null;
        }

        var radiusParamId = mepInfo.GetAssociateFamilyParameterId(new ElementId(BuiltInParameter.CONNECTOR_RADIUS));
        var diamParamId = mepInfo.GetAssociateFamilyParameterId(new ElementId(BuiltInParameter.CONNECTOR_DIAMETER));

        SmartConLogger.Debug($"  GetAssociateFamilyParameterId: CONNECTOR_RADIUS → id={radiusParamId.GetValue()}, CONNECTOR_DIAMETER → id={diamParamId.GetValue()}");

        bool useRadius = radiusParamId.GetValue() > 0;
        bool useDiameter = !useRadius && diamParamId.GetValue() > 0;

        if (!useRadius && !useDiameter)
        {
            SmartConLogger.Debug("  no bound parameter to CONNECTOR_RADIUS/DIAMETER → no size binding");
            return null;
        }

        var activeParamId = useRadius ? radiusParamId : diamParamId;
        var paramName = doc.GetElement(activeParamId)?.Name;
        if (string.IsNullOrEmpty(paramName))
        {
            SmartConLogger.Debug($"  ParameterElement id={activeParamId.GetValue()} not found or nameless → no size binding");
            SmartConLogger.Warn($"Connector on {connector.Owner?.Id.GetValue()}: bound ParameterElement id={activeParamId.GetValue()} missing in project " +
                $"[Action: проверьте семейство — привязка параметра размера коннектора повреждена, переукажите её в редакторе семейства]");
            return null;
        }

        SmartConLogger.Debug($"  Size binding: '{paramName}' (isDiameter={useDiameter}, paramId={activeParamId.GetValue()})");
        return (paramName!, useDiameter);
    }
}

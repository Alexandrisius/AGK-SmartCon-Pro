using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Why an element has no connectors a chain or a relation can walk. An empty
/// <c>connectors: []</c> alone reads as "nothing is connected"; the reason tells
/// the model that the element can never have a connection.
/// </summary>
internal static class AgentConnectorNotes
{
    /// <summary>
    /// Connectors of the element that can connect two elements: Curve connectors
    /// (cut-ins, I-08) are skipped. Null when the element has no connector manager.
    /// </summary>
    internal static int? CountConnectable(Element element)
    {
        var manager = AgentRelationsSupport.GetConnectorManager(element);
        if (manager is null)
            return null;

        var count = 0;
        foreach (Connector connector in manager.Connectors)
        {
            if (connector.ConnectorType != ConnectorType.Curve)
                count++;
        }

        return count;
    }

    /// <summary>Null when the element has connectors a relation can walk.</summary>
    internal static string? DescribeAbsence(Element element)
    {
        var manager = AgentRelationsSupport.GetConnectorManager(element);
        var category = element.Category is null
            ? string.Empty
            : AgentElementCardFactory.GetCategoryName(element.Category) ?? string.Empty;

        if (manager is null)
        {
            return "no MEP connectors: the element" +
                   (category.Length == 0 ? string.Empty : " (category " + category + ")") +
                   " carries none — it is not a pipe, duct, cable tray, conduit, or a " +
                   "fitting/equipment family that defines connectors";
        }

        var curveOnly = false;
        foreach (Connector connector in manager.Connectors)
        {
            if (connector.ConnectorType == ConnectorType.Curve)
                curveOnly = true;
        }

        return curveOnly
            ? "only curve (cut-in) connectors: they cannot connect one element to another"
            : "the element's family defines no connectors";
    }
}

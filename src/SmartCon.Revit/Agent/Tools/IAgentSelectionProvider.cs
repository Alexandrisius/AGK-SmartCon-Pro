using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent.Tools;

/// <summary>
/// Supplies the current UI selection to agent tools. Kept free of
/// RevitAPIUI types so that <c>SmartCon.Revit</c> agent tooling stays
/// loadable in the integration-test host, which has no UI session.
/// Implemented in <c>SmartCon.App</c> on top of <c>IRevitUIContext</c>.
/// </summary>
public interface IAgentSelectionProvider
{
    /// <summary>
    /// Current selection, or <see langword="null"/> when no UI document
    /// or selection is available.
    /// </summary>
    IReadOnlyList<ElementId>? GetSelectedElementIds();
}
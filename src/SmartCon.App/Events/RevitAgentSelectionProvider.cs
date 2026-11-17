#if REVIT2023_OR_GREATER
using Autodesk.Revit.DB;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Context;

namespace SmartCon.App.Events;

internal sealed class RevitAgentSelectionProvider : IAgentSelectionProvider
{
    private readonly IRevitUIContext _uiContext;

    public RevitAgentSelectionProvider(IRevitUIContext uiContext)
    {
        _uiContext = uiContext ?? throw new ArgumentNullException(nameof(uiContext));
    }

    public IReadOnlyList<ElementId>? GetSelectedElementIds()
    {
        var selection = _uiContext.GetUIApplication().ActiveUIDocument?.Selection;
        return selection?.GetElementIds().ToList();
    }
}
#endif
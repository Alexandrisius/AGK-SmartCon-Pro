using Autodesk.Revit.UI;
using SmartCon.Agent.Views;

namespace SmartCon.Agent;

public sealed class AgentPaneProvider : IDockablePaneProvider
{
    private readonly AgentPaneControl _control;

    public AgentPaneProvider(AgentPaneControl control)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = _control;
        data.VisibleByDefault = false;
        data.InitialState = new DockablePaneState
        {
            DockPosition = DockPosition.Right,
            MinimumWidth = 340,
            MinimumHeight = 400
        };
    }
}
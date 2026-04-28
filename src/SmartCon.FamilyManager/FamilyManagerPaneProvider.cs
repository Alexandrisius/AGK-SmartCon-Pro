using Autodesk.Revit.UI;
using SmartCon.FamilyManager.Views;

namespace SmartCon.FamilyManager;

public sealed class FamilyManagerPaneProvider : IDockablePaneProvider
{
    private readonly FamilyManagerPaneControl _control;

    public FamilyManagerPaneProvider(FamilyManagerPaneControl control)
    {
        _control = control;
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = _control;
        data.VisibleByDefault = false;
        data.InitialState = new DockablePaneState
        {
            DockPosition = DockPosition.Tabbed,
            TabBehind = DockablePanes.BuiltInDockablePanes.ProjectBrowser
        };
    }
}

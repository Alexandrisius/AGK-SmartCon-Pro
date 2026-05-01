using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SmartCon.FamilyManager.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class FamilyManagerCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var paneId = FamilyManagerPaneIds.FamilyManagerPane;
        var dockablePane = commandData.Application.GetDockablePane(paneId);
        if (dockablePane.IsShown())
            dockablePane.Hide();
        else
            dockablePane.Show();
        return Result.Succeeded;
    }
}

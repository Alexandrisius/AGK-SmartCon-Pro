using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
#if NET8_0_OR_GREATER
using CommandBase = Nice3point.Revit.Toolkit.External.ExternalCommand;
#else
using CommandBase = Autodesk.Revit.UI.IExternalCommand;
#endif

namespace SmartCon.Agent.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class AgentCommand : CommandBase
{
#if NET8_0_OR_GREATER
    public override void Execute()
    {
        Result = ExecuteCore(Application, out var message);
        ErrorMessage = message;
    }
#else
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        return ExecuteCore(commandData.Application, out message);
    }
#endif

    private static Result ExecuteCore(UIApplication uiApp, out string message)
    {
        message = string.Empty;
        try
        {
            var dockablePane = uiApp.GetDockablePane(AgentPaneIds.AgentPane);
            if (dockablePane.IsShown())
                dockablePane.Hide();
            else
                dockablePane.Show();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                "AgentCommand (toggle dockable pane) failed: " +
                ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
            message = ex.Message;
            return Result.Failed;
        }
    }
}
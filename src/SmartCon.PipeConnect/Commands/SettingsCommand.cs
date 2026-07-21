using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Services;
#if NET8_0_OR_GREATER
using CommandBase = Nice3point.Revit.Toolkit.External.ExternalCommand;
#else
using CommandBase = Autodesk.Revit.UI.IExternalCommand;
#endif

namespace SmartCon.PipeConnect.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class SettingsCommand : CommandBase
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
            CommandHelper.InitializeContext(uiApp);
            var doc = CommandHelper.GetDocument();

            var factory = ServiceHost.GetService<ISettingsViewModelFactory>();
            var vm = factory.Create(doc);

            var presenter = ServiceHost.GetService<IDialogPresenter>();
            presenter.ShowDialog(vm);

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"SettingsCommand failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            message = ex.Message;
            return Result.Failed;
        }
    }
}

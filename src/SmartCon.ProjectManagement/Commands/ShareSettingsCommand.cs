using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Services;
using SmartCon.ProjectManagement.Views;
#if NET8_0_OR_GREATER
using CommandBase = Nice3point.Revit.Toolkit.External.ExternalCommand;
#else
using CommandBase = Autodesk.Revit.UI.IExternalCommand;
#endif

namespace SmartCon.ProjectManagement.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class ShareSettingsCommand : CommandBase
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
        using var _scope = SmartConLogger.BeginScope("ShareSettings",
            ("Method", "Execute"));
        try
        {
            SmartConLogger.Info("ShareSettingsCommand started.");

            CommandHelper.InitializeContext(uiApp);
            var doc = CommandHelper.GetDocument();

            var factory = ServiceHost.GetService<Services.IShareSettingsViewModelFactory>();
            var vm = factory.Create(doc);
            var view = new ShareSettingsView(vm);
            new WindowInteropHelper(view).Owner = uiApp.MainWindowHandle;
            view.ShowDialog();

            SmartConLogger.Info("ShareSettingsCommand closed.");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"ShareSettingsCommand exception: {ex}");
            message = ex.Message;
            return Result.Failed;
        }
    }
}

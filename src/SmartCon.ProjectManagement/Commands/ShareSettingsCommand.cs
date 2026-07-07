using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Services;
using SmartCon.ProjectManagement.Views;

namespace SmartCon.ProjectManagement.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class ShareSettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        using var _scope = SmartConLogger.BeginScope("ShareSettings",
            ("Method", "Execute"));
        try
        {
            SmartConLogger.Info("ShareSettingsCommand started.");

            CommandHelper.InitializeContext(commandData.Application);
            var doc = CommandHelper.GetDocument();

            var factory = ServiceHost.GetService<Services.IShareSettingsViewModelFactory>();
            var vm = factory.Create(doc);
            var view = new ShareSettingsView(vm);
            new WindowInteropHelper(view).Owner = commandData.Application.MainWindowHandle;
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


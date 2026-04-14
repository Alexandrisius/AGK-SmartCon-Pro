using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.PipeConnect.Views;

namespace SmartCon.PipeConnect.Commands;

/// <summary>Opens the About dialog showing version info and update controls.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class AboutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var updateService = ServiceHost.GetService<IUpdateService>();
            var updateSettings = ServiceHost.GetService<IUpdateSettingsRepository>();

            var vm = new AboutViewModel(updateService, updateSettings);
            var view = new AboutView(vm);
            view.Show();

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}

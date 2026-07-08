using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Services;

namespace SmartCon.PipeConnect.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            CommandHelper.InitializeContext(commandData.Application);
            var doc = CommandHelper.GetDocument();

            var factory = ServiceHost.GetService<ISettingsViewModelFactory>();
            var vm = factory.Create(doc);

            var presenter = ServiceHost.GetService<IDialogPresenter>();
            presenter.ShowDialog(vm);

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Services;

namespace SmartCon.PipeConnect.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class AboutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            CommandHelper.InitializeContext(commandData.Application);

            var factory = ServiceHost.GetService<IAboutViewModelFactory>();
            var vm = factory.Create();

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

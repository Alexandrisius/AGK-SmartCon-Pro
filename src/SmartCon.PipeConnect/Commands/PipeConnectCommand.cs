using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.Views;

namespace SmartCon.PipeConnect.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class PipeConnectCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var contextWriter = ServiceHost.GetService<IRevitContextWriter>();
            contextWriter.SetContext(commandData.Application);

            var revitContext = (IRevitContext)contextWriter;
            var doc = revitContext.GetDocument();

            var factory = ServiceHost.GetService<IPipeConnectViewModelFactory>();

            var builder = factory.CreateSessionBuilder();

            var sessionCtx = builder.BuildSession(doc);
            if (sessionCtx is null) return Result.Cancelled;

            var vm = factory.CreateEditorViewModel(sessionCtx, doc);

            vm.Init();

            var view = new PipeConnectEditorView(vm);
            view.ShowDialog();

            return Result.Succeeded;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}

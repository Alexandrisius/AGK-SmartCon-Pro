using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.Views;
using System.Windows.Interop;

namespace SmartCon.PipeConnect.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class PipeConnectCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var sessionStart = DateTime.Now;
        SmartConLogger.LogSessionStart("PipeConnect");
        try
        {
            CommandHelper.InitializeContext(commandData.Application);
            var doc = CommandHelper.GetDocument();

            var factory = ServiceHost.GetService<IPipeConnectViewModelFactory>();

            var builder = factory.CreateSessionBuilder();

            var sessionCtx = builder.BuildSession(doc);
            if (sessionCtx is null) return Result.Cancelled;

            var vm = factory.CreateEditorViewModel(sessionCtx, doc);

            vm.Init();

            var view = new PipeConnectEditorView(vm);
            new WindowInteropHelper(view).Owner = commandData.Application.MainWindowHandle;
            view.ShowDialog();

            return Result.Succeeded;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"PipeConnect command failed: {ex.GetType().Name}: {ex.Message}");
            message = ex.Message;
            return Result.Failed;
        }
        finally
        {
            SmartConLogger.LogSessionEnd("PipeConnect", sessionStart);
        }
    }
}

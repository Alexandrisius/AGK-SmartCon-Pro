using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.PipeConnect.Views;

namespace SmartCon.PipeConnect.Commands;

/// <summary>
/// Entry point for PipeConnect from Ribbon.
/// Delegates S1–S6 analysis to <see cref="PipeConnectSessionBuilder"/>,
/// then opens the modal editor window.
/// All model changes execute inside a single TransactionGroup in the ViewModel.
/// Cancel = full RollBack().
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class PipeConnectCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var contextWriter = ServiceHost.GetService<IRevitContextWriter>();
            contextWriter.SetContext(commandData.Application);

            var revitContext = ServiceHost.GetService<IRevitContext>();
            var doc = revitContext.GetDocument();

            var builder = new PipeConnectSessionBuilder(
                ServiceHost.GetService<IElementSelectionService>(),
                ServiceHost.GetService<IConnectorService>(),
                ServiceHost.GetService<IFittingMappingRepository>(),
                ServiceHost.GetService<IFamilyConnectorService>(),
                ServiceHost.GetService<ITransactionService>(),
                ServiceHost.GetService<IDialogService>(),
                ServiceHost.GetService<IParameterResolver>(),
                ServiceHost.GetService<ILookupTableService>(),
                ServiceHost.GetService<IFittingMapper>(),
                ServiceHost.GetService<IElementChainIterator>());

            var sessionCtx = builder.BuildSession(doc);
            if (sessionCtx is null) return Result.Cancelled;

            var txService = ServiceHost.GetService<ITransactionService>();
            var connectorSvc = ServiceHost.GetService<IConnectorService>();
            var transformSvc = ServiceHost.GetService<ITransformService>();
            var fittingInsertSvc = ServiceHost.GetService<IFittingInsertService>();
            var paramResolver = ServiceHost.GetService<IParameterResolver>();
            var sizeResolver = ServiceHost.GetService<IDynamicSizeResolver>();
            var networkMover = ServiceHost.GetService<INetworkMover>();
            var mappingRepo = ServiceHost.GetService<IFittingMappingRepository>();
            var dialogSvc = ServiceHost.GetService<IDialogService>();
            var familyConnSvc = ServiceHost.GetService<IFamilyConnectorService>();

            var vm = new PipeConnectEditorViewModel(
                sessionCtx, doc, txService, connectorSvc, transformSvc,
                fittingInsertSvc, paramResolver, sizeResolver, networkMover,
                mappingRepo, dialogSvc, familyConnSvc);

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

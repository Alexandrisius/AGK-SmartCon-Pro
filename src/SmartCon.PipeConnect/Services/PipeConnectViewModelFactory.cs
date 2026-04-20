using Autodesk.Revit.DB;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Services;

public sealed class PipeConnectViewModelFactory(
    IElementSelectionService selectionSvc,
    IConnectorService connectorSvc,
    IFittingMappingRepository mappingRepo,
    IFamilyConnectorService familyConnSvc,
    ITransactionService txService,
    IDialogService dialogSvc,
    IParameterResolver paramResolver,
    ILookupTableService lookupSvc,
    IFittingMapper fittingMapper,
    IElementChainIterator chainIterator,
    IFittingChainResolver chainResolver,
    ITransformService transformSvc,
    IAlignmentService alignmentSvc,
    IFittingInsertService fittingInsertSvc,
    IDynamicSizeResolver sizeResolver,
    INetworkMover networkMover,
    ChainOperationHandler chainOpHandler,
    PipeConnectRotationHandler rotationHandler,
    DynamicSizeLoader sizeLoader) : IPipeConnectViewModelFactory
{
    public PipeConnectSessionBuilder CreateSessionBuilder() => new(
        selectionSvc, connectorSvc, mappingRepo, familyConnSvc,
        txService, dialogSvc, paramResolver, lookupSvc,
        fittingMapper, chainIterator, chainResolver);

    public PipeConnectEditorViewModel CreateEditorViewModel(PipeConnectSessionContext sessionCtx, Document doc) => new(
        sessionCtx, doc, txService, connectorSvc, transformSvc, alignmentSvc,
        fittingInsertSvc, paramResolver, sizeResolver, networkMover,
        mappingRepo, dialogSvc, familyConnSvc, fittingMapper,
        chainOpHandler, rotationHandler, sizeLoader);
}

using Autodesk.Revit.UI;
using Microsoft.Extensions.DependencyInjection;
using SmartCon.Core.Math.FormulaEngine.Solver;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Events;
using SmartCon.PipeConnect.Services;
using SmartCon.Revit.Context;
using SmartCon.Revit.Events;
using SmartCon.Revit.Family;
using SmartCon.Revit.Fittings;
using SmartCon.Revit.Network;
using SmartCon.Revit.Parameters;
using SmartCon.Revit.Selection;
using SmartCon.Revit.Transform;
using SmartCon.Revit.Transactions;
using SmartCon.Revit.Updates;

namespace SmartCon.App.DI;

/// <summary>
/// Регистрация всех сервисов в DI-контейнере.
/// Связывает интерфейсы Core с реализациями Revit.
/// </summary>
public static class ServiceRegistrar
{
    public static void Register(IServiceCollection services, UIControlledApplication app)
    {
        // --- Context ---
        var revitContext = new RevitContext();
        services.AddSingleton<IRevitContext>(revitContext);
        services.AddSingleton<IRevitContextWriter>(revitContext);
        services.AddSingleton<IRevitUIContext>(revitContext);  // Phase 2: UIDocument access

        // --- Transactions ---
        services.AddSingleton<ITransactionService, RevitTransactionService>();

        // --- Selection (Phase 2) ---
        services.AddSingleton<IElementSelectionService, ElementSelectionService>();
        services.AddSingleton<IConnectorService, ConnectorService>();

        // --- Transform (Phase 2) ---
        services.AddSingleton<ITransformService, RevitTransformService>();

        // --- Mapping & Family (Phase 3) ---
        services.AddSingleton<IFittingMappingRepository, JsonFittingMappingRepository>();
        services.AddSingleton<IFamilyConnectorService, RevitFamilyConnectorService>();
        services.AddSingleton<IFittingFamilyRepository, FittingFamilyRepository>();
        services.AddSingleton<IDialogService, PipeConnectDialogService>();

        // --- Parameter Resolution (Phase 4) ---
        services.AddSingleton<IParameterResolver, RevitParameterResolver>();
        services.AddSingleton<ILookupTableService, RevitLookupTableService>();
        services.AddSingleton<IDynamicSizeResolver, RevitDynamicSizeResolver>();

        // --- Formula Solver (Phase 6) ---
        services.AddSingleton<IFormulaSolver, FormulaSolver>();

        // --- Fitting System (Phase 5) ---
        services.AddSingleton<IFittingMapper, FittingMapper>();
        services.AddSingleton<IFittingInsertService, RevitFittingInsertService>();

        // --- Chain (Phase 7) ---
        services.AddSingleton<IElementChainIterator, ElementChainIterator>();
        services.AddSingleton<INetworkMover, NetworkMover>();

        // --- External Events (ADR-008: generic) ---
        var genericHandler = new ActionExternalEventHandler(revitContext);
        var genericEvent = ExternalEvent.Create(genericHandler);
        services.AddSingleton(genericHandler);
        services.AddSingleton(genericEvent);

        // --- External Events (Phase 8: PipeConnect modeless editor) ---
        var pipeConnectHandler = new PipeConnectExternalEvent(revitContext);
        var pipeConnectEvent   = ExternalEvent.Create(pipeConnectHandler);
        pipeConnectHandler.Initialize(pipeConnectEvent);
        services.AddSingleton(pipeConnectHandler);

        // --- Update Service ---
        services.AddSingleton<IUpdateSettingsRepository, JsonUpdateSettingsRepository>();
        services.AddSingleton<IUpdateService, GitHubUpdateService>();
    }
}

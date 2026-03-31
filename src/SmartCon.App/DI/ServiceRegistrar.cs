using Autodesk.Revit.UI;
using Microsoft.Extensions.DependencyInjection;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Events;
using SmartCon.PipeConnect.Services;
using SmartCon.Core.Services.Implementation;
using SmartCon.Revit.Context;
using SmartCon.Revit.Events;
using SmartCon.Revit.Family;
using SmartCon.Revit.Parameters;
using SmartCon.Revit.Selection;
using SmartCon.Revit.Transform;
using SmartCon.Revit.Transactions;

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

        // --- External Events (ADR-008: generic) ---
        var genericHandler = new ActionExternalEventHandler(revitContext);
        var genericEvent = ExternalEvent.Create(genericHandler);
        services.AddSingleton(genericHandler);
        services.AddSingleton(genericEvent);

        // --- External Events (Phase 2: PipeConnect-specific handler) ---
        // ExternalEvent создаётся и передаётся в Phase 6 (WPF-слой).
        // В Phase 2 PipeConnectCommand работает напрямую на Revit-потоке.
        services.AddSingleton<PipeConnectExternalEvent>();
    }
}

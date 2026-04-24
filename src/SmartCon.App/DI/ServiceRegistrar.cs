using Autodesk.Revit.UI;
using Microsoft.Extensions.DependencyInjection;
using SmartCon.Core.Math.FormulaEngine.Solver;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Events;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.PipeConnect.Views;
using SmartCon.Revit.Context;
using SmartCon.Revit.Events;
using SmartCon.Revit.Family;
using SmartCon.Revit.Fittings;
using SmartCon.Revit.Network;
using SmartCon.Revit.Parameters;
using SmartCon.Revit.Selection;
using SmartCon.ProjectManagement.Services;
using SmartCon.ProjectManagement.ViewModels;
using SmartCon.ProjectManagement.Views;
using SmartCon.Revit.Sharing;
using SmartCon.Revit.Storage;
using SmartCon.Revit.Transactions;
using SmartCon.Revit.Transform;
using SmartCon.Revit.Updates;
using ShareSettingsView = SmartCon.ProjectManagement.Views.ShareSettingsView;
using ShareSettingsViewModel = SmartCon.ProjectManagement.ViewModels.ShareSettingsViewModel;

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
        services.AddSingleton<IAlignmentService, RevitAlignmentService>();

        // --- Mapping & Family (ADR-012: per-project ExtensibleStorage) ---
        services.AddSingleton<IFittingMappingRepository, RevitFittingMappingRepository>();
        services.AddSingleton<IFamilyConnectorService, RevitFamilyConnectorService>();
        services.AddSingleton<IFittingFamilyRepository, FittingFamilyRepository>();
        services.AddSingleton<IDialogService, PipeConnectDialogService>();

        // --- Parameter Resolution (Phase 4) ---
        services.AddSingleton<IParameterResolver, RevitParameterResolver>();
        services.AddSingleton<ILookupTableService, RevitLookupTableService>();
        services.AddSingleton<FamilySymbolSizeExtractor>();
        services.AddSingleton<IDynamicSizeResolver, RevitDynamicSizeResolver>();

        // --- Formula Solver (Phase 6) ---
        services.AddSingleton<IFormulaSolver, FormulaSolver>();

        // --- Fitting System (Phase 5) ---
        services.AddSingleton<IFittingMapper, FittingMapper>();
        services.AddSingleton<IFittingInsertService, RevitFittingInsertService>();
        services.AddSingleton<IFittingChainResolver, FittingChainResolver>();

        // --- Chain (Phase 7) ---
        services.AddSingleton<IElementChainIterator, ElementChainIterator>();
        services.AddSingleton<INetworkMover, NetworkMover>();

        // --- PipeConnect Helper Services (A-2: DI instead of new in ViewModel) ---
        services.AddSingleton<CtcResolutionService>();
        services.AddSingleton<CtcGuessService>();
        services.AddSingleton<CtcFamilyWriter>();
        services.AddSingleton<FittingCtcManager>();
        services.AddSingleton<ChainOperationHandler>();
        services.AddSingleton<PipeConnectRotationHandler>();
        services.AddSingleton<DynamicSizeLoader>();

        // --- External Events (ADR-008: generic) ---
        var genericHandler = new ActionExternalEventHandler(revitContext);
        var genericEvent = ExternalEvent.Create(genericHandler);
        services.AddSingleton(genericHandler);
        services.AddSingleton(genericEvent);

        // --- External Events (Phase 8: PipeConnect modeless editor) ---
        var pipeConnectHandler = new PipeConnectExternalEvent(revitContext);
        var pipeConnectEvent = ExternalEvent.Create(pipeConnectHandler);
        pipeConnectHandler.Initialize(pipeConnectEvent);
        services.AddSingleton(pipeConnectHandler);

        // --- Update Service ---
        services.AddSingleton<IUpdateSettingsRepository, JsonUpdateSettingsRepository>();
        services.AddSingleton<IUpdateService, GitHubUpdateService>();

        // --- ViewModel Factories (A-1: eliminate Service Locator in Commands) ---
        services.AddSingleton<IPipeConnectViewModelFactory, PipeConnectViewModelFactory>();
        services.AddSingleton<IAboutViewModelFactory, AboutViewModelFactory>();
        services.AddSingleton<ISettingsViewModelFactory, SettingsViewModelFactory>();

        // --- Dialog Presenter (C-3: VM→View mapping, decoupling from concrete Views) ---
        services.AddSingleton(_ =>
        {
            var presenter = new WpfDialogPresenter();
            presenter.Register<MiniTypeSelectorViewModel>(vm => new MiniTypeSelectorView(vm));
            presenter.Register<FamilySelectorViewModel>(vm => new FamilySelectorView(vm));
            presenter.Register<AboutViewModel>(vm => new AboutView(vm));
            presenter.Register<MappingEditorViewModel>(vm => new MappingEditorView(vm));
            presenter.Register<PipeConnectEditorViewModel>(vm => new PipeConnectEditorView(vm));
            presenter.Register<ShareSettingsViewModel>(vm => new ShareSettingsView(vm));
            presenter.Register<ExportNameDialogViewModel>(vm => new ExportNameDialog(vm));
            presenter.Register<ParseRuleViewModel>(vm => new ParseRuleView(vm));
            presenter.Register<FieldLibraryViewModel>(vm => new FieldLibraryView(vm));
            presenter.Register<AllowedValuesViewModel>(vm => new AllowedValuesView(vm));
            return presenter;
        });
        services.AddSingleton<IDialogPresenter>(sp => sp.GetRequiredService<WpfDialogPresenter>());

        // --- ProjectManagement (Phase 11) ---
        services.AddSingleton<IShareProjectSettingsRepository, RevitShareProjectSettingsRepository>();
        services.AddSingleton<IModelPurgeService, RevitModelPurgeService>();
        services.AddSingleton<IFileNameParser, RevitFileNameParser>();
        services.AddSingleton<IViewRepository, RevitViewRepository>();
        services.AddSingleton<IShareSettingsViewModelFactory, ShareSettingsViewModelFactory>();
    }
}

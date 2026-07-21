using Autodesk.Revit.UI;
using Microsoft.Extensions.DependencyInjection;
using SmartCon.App.Events;
using SmartCon.App.Services;
using SmartCon.Core.Math.FormulaEngine.Solver;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager;
using SmartCon.FamilyManager.Events;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.FamilyManager.Views;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.PipeConnect.Views;
using SmartCon.ProjectManagement.Services;
using SmartCon.ProjectManagement.ViewModels;
using SmartCon.ProjectManagement.Views;
using SmartCon.Revit.Context;
using SmartCon.Revit.Events;
using SmartCon.Revit.Family;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Fittings;
using SmartCon.Revit.Network;
using SmartCon.Revit.Parameters;
using SmartCon.Revit.Selection;
using SmartCon.Revit.Sharing;
using SmartCon.Revit.Storage;
using SmartCon.Revit.Transactions;
using SmartCon.Revit.Transform;
using SmartCon.Revit.Updates;
using StaleCategoryAggregator = SmartCon.FamilyManager.Services.Stale.StaleCategoryAggregator;
using StaleDetector = SmartCon.FamilyManager.Services.Stale.StaleDetector;
using StaleFamilyUpdater = SmartCon.FamilyManager.Services.Stale.StaleFamilyUpdater;
using FamilyVersionWriter = SmartCon.FamilyManager.Services.Stale.FamilyVersionWriter;
using ShareSettingsView = SmartCon.ProjectManagement.Views.ShareSettingsView;
using ShareSettingsViewModel = SmartCon.ProjectManagement.ViewModels.ShareSettingsViewModel;
using FmParseRuleView = SmartCon.FamilyManager.Views.ParseRuleView;
using FmParseRuleViewModel = SmartCon.FamilyManager.ViewModels.ProjectBase.ParseRuleViewModel;
using FmFieldLibraryView = SmartCon.FamilyManager.Views.FieldLibraryView;
using FmFieldLibraryViewModel = SmartCon.FamilyManager.ViewModels.ProjectBase.FieldLibraryViewModel;
using FmAllowedValuesView = SmartCon.FamilyManager.Views.AllowedValuesView;
using FmAllowedValuesViewModel = SmartCon.FamilyManager.ViewModels.ProjectBase.AllowedValuesViewModel;
using FmProjectBaseRulesEditorView = SmartCon.FamilyManager.Views.ProjectBaseRulesEditorView;
using FmProjectBaseRulesEditorViewModel = SmartCon.FamilyManager.ViewModels.ProjectBase.ProjectBaseRulesEditorViewModel;
using PmParseRuleView = SmartCon.ProjectManagement.Views.ParseRuleView;
using PmParseRuleViewModel = SmartCon.ProjectManagement.ViewModels.ParseRuleViewModel;
using PmFieldLibraryView = SmartCon.ProjectManagement.Views.FieldLibraryView;
using PmFieldLibraryViewModel = SmartCon.ProjectManagement.ViewModels.FieldLibraryViewModel;
using PmAllowedValuesView = SmartCon.ProjectManagement.Views.AllowedValuesView;
using PmAllowedValuesViewModel = SmartCon.ProjectManagement.ViewModels.AllowedValuesViewModel;

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

        // --- Update Service ---
        services.AddSingleton<IUpdateSettingsRepository, JsonUpdateSettingsRepository>();
        services.AddSingleton<IUpdateService, GitHubUpdateService>();

        // --- ViewModel Factories (A-1: eliminate Service Locator in Commands) ---
        services.AddSingleton<IPipeConnectViewModelFactory, PipeConnectViewModelFactory>();
        services.AddSingleton<IAboutViewModelFactory, AboutViewModelFactory>();
        services.AddSingleton<ISettingsViewModelFactory, SettingsViewModelFactory>();

        // --- Dialog Presenter (C-3: VM→View mapping, decoupling from concrete Views) ---
        services.AddSingleton(sp =>
        {
            var presenter = new WpfDialogPresenter(sp.GetRequiredService<IRevitContext>());
            presenter.Register<MiniTypeSelectorViewModel>(vm => new MiniTypeSelectorView(vm));
            presenter.Register<FamilySelectorViewModel>(vm => new FamilySelectorView(vm));
            presenter.Register<AboutViewModel>(vm => new AboutView(vm));
            presenter.Register<MappingEditorViewModel>(vm => new MappingEditorView(vm));
            presenter.Register<PipeConnectEditorViewModel>(vm => new PipeConnectEditorView(vm));
            presenter.Register<ShareSettingsViewModel>(vm => new ShareSettingsView(vm));
            presenter.Register<ShareResultViewModel>(vm => new ShareResultView(vm));
            presenter.Register<ExportNameDialogViewModel>(vm => new ExportNameDialog(vm));
            presenter.Register<PmParseRuleViewModel>(vm => new PmParseRuleView(vm));
            presenter.Register<PmFieldLibraryViewModel>(vm => new PmFieldLibraryView(vm));
            presenter.Register<PmAllowedValuesViewModel>(vm => new PmAllowedValuesView(vm));
            presenter.Register<CategoryTreeEditorViewModel>(vm => new CategoryTreeEditorView(vm));
            presenter.Register<CategoryPickerViewModel>(vm => new CategoryPickerView(vm));
            presenter.Register<FamilyPropertiesViewModel>(vm => new FamilyPropertiesView(vm));
            presenter.Register<CropAvatarViewModel>(vm => new CropAvatarView(vm));
            presenter.Register<AttributeLibraryViewModel>(vm => new AttributeLibraryView(vm));
            presenter.Register<ProfileViewModel>(vm => new ProfileView(vm));
            presenter.Register<FamilyBatchImportViewModel>(vm => new FamilyBatchImportView(vm));
            presenter.Register<SharedFamiliesLoadModeDialogViewModel>(vm => new SharedFamiliesLoadModeDialogView(vm));
            presenter.Register<DatabaseUpdateProgressViewModel>(vm => new DatabaseUpdateProgressView(vm));
            presenter.Register<FmProjectBaseRulesEditorViewModel>(vm => new FmProjectBaseRulesEditorView(vm));
            presenter.Register<FmParseRuleViewModel>(vm => new FmParseRuleView(vm));
            presenter.Register<FmFieldLibraryViewModel>(vm => new FmFieldLibraryView(vm));
            presenter.Register<FmAllowedValuesViewModel>(vm => new FmAllowedValuesView(vm));
            return presenter;
        });
        services.AddSingleton<IDialogPresenter>(sp => sp.GetRequiredService<WpfDialogPresenter>());

        // --- ProjectManagement (Phase 11) ---
        services.AddSingleton<IShareProjectSettingsRepository, RevitShareProjectSettingsRepository>();
        services.AddSingleton<IModelPurgeService, RevitModelPurgeService>();
        services.AddSingleton<IFileNameParser, RevitFileNameParser>();
        services.AddSingleton<IViewRepository, RevitViewRepository>();
        services.AddSingleton<IShareSettingsViewModelFactory, ShareSettingsViewModelFactory>();

        services.AddSingleton<IUiFreezeRecoveryService, RevitUiFreezeRecoveryService>();

        // --- FamilyManager (Phase 13) ---
        services.AddSingleton<LocalCatalogDatabase>();
        services.AddSingleton<ILocalCatalogMigrator, LocalCatalogMigrator>();
        services.AddSingleton<FamilyManagerServices>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, GuidIdGenerator>();
        services.AddSingleton<IDispatcher, SmartCon.FamilyManager.UI.WpfDispatcher>();
        services.AddSingleton<StoragePathResolver>();
        services.AddSingleton<LocalCatalogProvider>();
        services.AddSingleton<IFamilyCatalogProvider>(sp => sp.GetRequiredService<LocalCatalogProvider>());
        services.AddSingleton<IWritableFamilyCatalogProvider>(sp => sp.GetRequiredService<LocalCatalogProvider>());
        services.AddSingleton<LocalCategoryRepository>();
        services.AddSingleton<ICategoryRepository>(sp => sp.GetRequiredService<LocalCategoryRepository>());
        services.AddSingleton<LocalFamilyTypeRepository>();
        services.AddSingleton<IFamilyTypeRepository>(sp => sp.GetRequiredService<LocalFamilyTypeRepository>());
        services.AddSingleton<IFamilyImportService, LocalFamilyImportService>();
        // v2.0.0: precomputer allocates the canonical
        // (CatalogItemId, VersionLabel, ManagedPath) triple for a given
        // display name without performing file I/O. Used by the batch
        // import dialog's rename handler so the round-trip from the
        // dialog back to ImportFileAsync always carries consistent
        // values (the dialog pre-build and the dialog rename share the
        // same single source of truth).
        services.AddSingleton<IFamilyImportPrecomputer, LocalFamilyImportPrecomputer>();
        services.AddSingleton<IFamilyFileResolver, LocalFamilyFileResolver>();
        services.AddSingleton<IFamilyAssetService, LocalFamilyAssetService>();
        services.AddSingleton<IAvatarCropService, WpfAvatarCropService>();
        services.AddSingleton<IAttributePresetService, LocalAttributePresetService>();
        services.AddSingleton<LocalAttributeDefinitionRepository>();
        services.AddSingleton<IAttributeDefinitionRepository>(sp => sp.GetRequiredService<LocalAttributeDefinitionRepository>());
        services.AddSingleton<LocalCategoryAttributeBindingService>();
        services.AddSingleton<ICategoryAttributeBindingService>(sp => sp.GetRequiredService<LocalCategoryAttributeBindingService>());
        services.AddSingleton<LocalAttributeValueRepository>();
        services.AddSingleton<IAttributeValueRepository>(sp => sp.GetRequiredService<LocalAttributeValueRepository>());
        services.AddSingleton<LocalFamilyDataImportRunRepository>();
        services.AddSingleton<IFamilyDataImportRunRepository>(sp => sp.GetRequiredService<LocalFamilyDataImportRunRepository>());
        services.AddSingleton<IFamilyManagerMetadataMediator, FamilyManagerMetadataMediator>();
        services.AddSingleton<ITypeCatalogValueApplier, TypeCatalogValueApplier>();
        services.AddSingleton<IFamilyDataExtractionService, RevitFamilyDataExtractionService>();
        services.AddSingleton<IFamilyTypeCatalogBaker, RevitFamilyTypeCatalogBaker>();
        services.AddSingleton<FamilyDataImportService>();
        services.AddSingleton<IFamilyDataImportService>(sp => sp.GetRequiredService<FamilyDataImportService>());
        services.AddSingleton<IFamilyLoadService, RevitFamilyLoadService>();
        services.AddSingleton<IRevitFileInfoReader, RevitFileInfoReader>();
        services.AddSingleton<LocalSharedNestedFamilyRepository>();
        services.AddSingleton<ISharedNestedFamilyRepository>(sp => sp.GetRequiredService<LocalSharedNestedFamilyRepository>());
        services.AddSingleton<IFamilyMetadataExtractionService, FileMetadataExtractionService>();
        services.AddSingleton<IFamilySearchService, RevitFamilySearchService>();
        services.AddSingleton<IFamilyPlacementService, RevitFamilyPlacementService>();
        services.AddSingleton<IFamilyPlacementDragService, RevitFamilyPlacementDragService>();
        services.AddSingleton<ILoadableFamilyScanner, LoadableFamilyScanner>();
        services.AddSingleton<ILoadableFamilyTypeResolver, LoadableFamilyTypeResolver>();
        services.AddSingleton<ILoadableFamilyImportOrchestrator, SmartCon.FamilyManager.Services.LoadableFamilyImportOrchestrator>();
        services.AddSingleton<ISystemFamilyRevitOperations, SystemFamilyRevitOperations>();
        services.AddSingleton<ISystemFamilyPlacementService, SystemFamilyPlacementService>();
        services.AddSingleton<ISystemFamilyIsolationProjectService, SmartCon.FamilyManager.Services.SystemFamilyIsolationProjectAdapter>();
        services.AddSingleton<ISystemFamilyAttributeExtractor, SmartCon.FamilyManager.Services.SystemFamilyAttributeExtractor>();
        services.AddSingleton<ISystemFamilyImportOrchestrator, SmartCon.FamilyManager.Services.SystemFamilyImportOrchestrator>();
        services.AddSingleton<ISystemFamilyAttributeExtractionService, SystemFamilyAttributeExtractionService>();
        services.AddSingleton<IActiveDocumentClassifier, ActiveDocumentClassifier>();
        services.AddSingleton<IUserIdentityService, RevitUserIdentityService>();
        services.AddSingleton<IDbUserRepository, LocalDbUserRepository>();
        services.AddSingleton<IDbAccessControlService, DbAccessControlService>();
        services.AddSingleton<IFamilyManagerDialogService, FamilyManagerDialogService>();

        // --- FamilyManager Content Hash Dedup (Phase 27 / Issue #88) ---
        services.AddSingleton<IFamilySnapshotExtractor, SmartCon.Revit.FamilyManager.RevitFamilySnapshotExtractor>();
        services.AddSingleton<IFamilyContentHasher, SmartCon.Core.Services.Implementation.FamilyContentHasher>();
        services.AddSingleton<IContentHashDedupService, SmartCon.FamilyManager.Services.ContentHashDedupService>();
        services.AddSingleton<SmartCon.FamilyManager.Services.FamilyImportPreparationService>();

        // --- FamilyManager Hash Recalculation Migration (Issue #126) ---
        services.AddSingleton<IFamilyMigrationExtractor, SmartCon.Revit.FamilyManager.RevitFamilyMigrationExtractor>();

        // --- Database actualization engine (ADR-054, docs/architecture/database-migrations.md) ---
        // THE single "update database" service: unions task detections,
        // opens each pending family file once, applies pending tasks.
        // New extraction-time features = one new IDatabaseActualizationTask
        // class registered below — engine/dialog/gate/resume/purge are free.
        services.AddSingleton<IDatabaseActualizationTask, SmartCon.FamilyManager.Services.Actualization.HashFormatActualizationTask>();
        services.AddSingleton<IDatabaseActualizationTask, SmartCon.FamilyManager.Services.Actualization.AttributesActualizationTask>();
        services.AddSingleton<IDatabaseActualizationTask, SmartCon.FamilyManager.Services.Actualization.GlbPreviewActualizationTask>();
        services.AddSingleton<ICatalogActualizationService, SmartCon.FamilyManager.Services.Actualization.CatalogActualizationService>();
        services.AddSingleton<IDatabaseUpdateStateService, SmartCon.FamilyManager.Services.Migrations.DatabaseUpdateStateService>();

        // --- FamilyManager 3D Geometry Preview (ADR-042 / Issue #92) ---
        services.AddSingleton<IFamilyGeometryExtractor, SmartCon.Revit.FamilyManager.RevitFamilyGeometryExtractor>();
        services.AddSingleton<IGlbWriter, SmartCon.FamilyManager.Services.Geometry.FamilyGeometryGlbWriter>();
        services.AddSingleton<IFamilyGeometryPipeline, SmartCon.FamilyManager.Services.Geometry.FamilyGeometryPipeline>();

        // --- FamilyManager Stale Detection (Phase 24 / ADR-030) ---
        services.AddSingleton<IFamilyVersionStore, RevitFamilyVersionStore>();
        services.AddSingleton<IFamilyVersionWriter, FamilyVersionWriter>();
        services.AddSingleton<IStaleDetector, StaleDetector>();
        services.AddSingleton<IStaleFamilyUpdater, StaleFamilyUpdater>();
        services.AddSingleton<IStaleCategoryAggregator, StaleCategoryAggregator>();
        services.AddSingleton<IFamilyFinder, RevitFamilyFinder>();

        services.AddSingleton<FamilyManagerMainViewModel>();
        services.AddSingleton<FamilyManagerPaneControl>();
        services.AddSingleton<FamilyManagerPaneProvider>();

        var windowFocusService = new RevitWindowFocusService(revitContext);
        services.AddSingleton<IWindowFocusService>(windowFocusService);

        // The awaitable queue is pure C# (testable in isolation).
        // The IExternalEventHandler adapter lives in SmartCon.App so
        // that SmartCon.FamilyManager does not need RevitAPIUI at
        // type-init time (unit-test requirement).
        var fmAwaitable = new FamilyManagerAwaitableEvent(revitContext, windowFocusService);
        services.AddSingleton(fmAwaitable);
        services.AddSingleton<IFamilyManagerAwaitableEvent>(fmAwaitable);

        var fmHandler = new RevitFamilyManagerAwaitableEvent(fmAwaitable);
        var fmEvent = ExternalEvent.Create(fmHandler);
        fmAwaitable.Initialize(() => fmEvent.Raise());

        services.AddSingleton<IFamilyStorageRenameService, LocalFamilyStorageRenameService>();
        services.AddSingleton<IFamilyManagerViewModelFactory, FamilyManagerViewModelFactory>();
        services.AddSingleton<IDatabaseManager, DatabaseManager>();

        // --- Project-base activation (Phase 30 / Issue #119) ---
        services.AddSingleton<IRegistryMigrator, RegistryMigrator>();
        services.AddSingleton<IProjectBaseBindingEvaluator, ProjectBaseBindingEvaluator>();
        services.AddSingleton<IProjectBaseActivator, ProjectBaseActivator>();
        // The notifier subscribes to UIControlledApplication.ViewActivated up
        // front (decision A2 of #119 — recommended by Jeremy Tammik since it
        // fires on both DocumentOpened and cross-document tab switches). It
        // also implements IDisposable, which the DI container invokes from
        // ServiceLocator.Dispose during OnShutdown to unsubscribe.
        var activeDocNotifier = new ActiveDocumentChangeNotifier();
        services.AddSingleton<IActiveDocumentChangeNotifier>(activeDocNotifier);
        activeDocNotifier.Register(app);
    }
}

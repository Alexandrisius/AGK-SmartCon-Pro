using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Events;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.Services.Stale;

namespace SmartCon.FamilyManager.Services;

public sealed record FamilyManagerServices(
    IFamilyCatalogProvider CatalogProvider,
    IWritableFamilyCatalogProvider WritableProvider,
    IFamilyImportService ImportService,
    /// <summary>
    /// v2.0.0: precomputer that allocates the canonical
    /// (CatalogItemId, VersionLabel, ManagedPath) triple for a given
    /// display name. Used by the batch-import dialog's rename handler
    /// so the round-trip from the dialog back to <c>ImportFileAsync</c>
    /// always carries consistent values (the dialog pre-build and the
    /// dialog rename share the same single source of truth).
    /// </summary>
    IFamilyImportPrecomputer ImportPrecomputer,
    /// <summary>
    /// v2.0.0: storage path resolver. Injected as a record member so
    /// <c>FamilyManagerMainViewModel.ProcessFamilyImportAsync</c> can
    /// call <c>EnsureFamilyDirectories</c> before <c>Document.SaveAs</c>
    /// — Revit requires the target directory to exist beforehand and
    /// ComputeManagedFilePath alone does not create it.
    /// </summary>
    StoragePathResolver PathResolver,
    IFamilyFileResolver FileResolver,
    IFamilyLoadService LoadService,
    IFamilyManagerDialogService DialogService,
    IFamilyManagerAwaitableEvent AwaitableEvent,
    IFamilyManagerViewModelFactory ViewModelFactory,
    IRevitContext RevitContext,
    IDatabaseManager DatabaseManager,
    ITransactionService TransactionService,
    ICategoryRepository CategoryRepository,
    IFamilyTypeRepository TypeRepository,
    IFamilyDataExtractionService ExtractionService,
    IFamilyDataImportService DataImportService,
    IDbAccessControlService AccessControl,
    IFamilySearchService FamilySearchService,
    IFamilyPlacementService FamilyPlacementService,
    IFamilyPlacementDragService PlacementDragService,
    IRevitFileInfoReader FileInfoReader,
    IFamilyMetadataExtractionService MetadataService,
    ISystemFamilyPlacementService SystemFamilyPlacementService,
    ISystemFamilyRevitOperations SystemFamilyRevitOps,
    ISystemFamilyIsolationProjectService SystemFamilyIsolationProject,
    ISystemFamilyAttributeExtractor SystemFamilyAttributeExtractor,
    ISystemFamilyImportOrchestrator SystemFamilyImportOrchestrator,
    IActiveDocumentClassifier ActiveDocumentClassifier,
    ILoadableFamilyScanner LoadableFamilyScanner,
    ILoadableFamilyImportOrchestrator LoadableFamilyImportOrchestrator,
    IFamilyVersionStore VersionStore,
    IStaleDetector StaleDetector,
    IStaleFamilyUpdater StaleUpdater,
    IStaleCategoryAggregator StaleCategoryAggregator,
    IFamilyFinder FamilyFinder,
    IFamilyVersionWriter VersionWriter,
    IClock Clock,
    ISharedNestedFamilyRepository SharedNestedRepository);

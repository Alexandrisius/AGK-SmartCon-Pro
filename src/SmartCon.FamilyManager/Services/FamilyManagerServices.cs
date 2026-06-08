using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Events;

namespace SmartCon.FamilyManager.Services;

public sealed record FamilyManagerServices(
    IFamilyCatalogProvider CatalogProvider,
    IWritableFamilyCatalogProvider WritableProvider,
    IFamilyImportService ImportService,
    IFamilyFileResolver FileResolver,
    IFamilyLoadService LoadService,
    IProjectFamilyUsageRepository UsageRepo,
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
    IActiveFamilyFilePreparer ActiveFamilyFilePreparer,
    IActiveDocumentClassifier ActiveDocumentClassifier,
    IActiveImportCleanupService ActiveImportCleanupService,
    ILoadableFamilyScanner LoadableFamilyScanner,
    ILoadableFamilyImportOrchestrator LoadableFamilyImportOrchestrator);

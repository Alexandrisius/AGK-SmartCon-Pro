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
    IFamilyAssetService AssetService,
    IFamilyMetadataExtractionService MetadataService,
    ISystemFamilyPlacementService SystemFamilyPlacementService,
    /// <summary>
    /// Issue #104: synchronizes system types in the project with the catalog
    /// mini-project (create-or-update + ES marker). Backs "Загрузить в
    /// проект" for system families, the placement fast-path check and the
    /// system branch of stale update.
    /// </summary>
    ISystemTypeSyncOrchestrator SystemSyncOrchestrator,
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
    ISharedNestedFamilyRepository SharedNestedRepository,
    /// <summary>
    /// v2.0.0 (ADR-036, M-019-003): UI dispatcher. Injected instead of
    /// <c>Application.Current?.Dispatcher</c> because the latter is
    /// <c>null</c> in net48 Revit addins (known WPF/Revit interaction
    /// bug, see ADR-025 M-019-003). The injected <see cref="IDispatcher"/>
    /// is unit-testable (<c>WpfDispatcher</c> is net48-safe) and is the
    /// canonical way to marshal FireAndForget callbacks back to the UI
    /// thread per ADR-031.
    /// </summary>
    IDispatcher Dispatcher,
    /// <summary>
    /// Phase 27 / Issue #88: content-hash dedup infrastructure.
    /// </summary>
    IFamilySnapshotExtractor SnapshotExtractor,
    IFamilyContentHasher ContentHasher,
    IContentHashDedupService DedupService,
    /// <summary>
    /// 3D-preview / white-dialog fix: platform-specific workaround that
    /// resyncs the WPF render thread after Revit API operations that can
    /// leave it in a zombie state on net48 (REVIT-236376 / REVIT-237190).
    /// </summary>
    IUiFreezeRecoveryService FreezeRecovery,
    FamilyImportPreparationService PreparationService,
    /// <summary>
    /// Phase 30 / Issue #119: cross-module notifier for "active Revit document
    /// changed". The VM subscribes in its ctor and un-subscribes in Dispose so
    /// it can drive project-base auto-activation when the user switches
    /// between open project files.
    /// </summary>
    IActiveDocumentChangeNotifier ActiveDocumentNotifier,
    /// <summary>
    /// Phase 30 / Issue #119: pure-C# service that, given the currently
    /// active Revit file path, picks and switches to the matching project base
    /// (or falls back to the first general base). Used by the VM in response
    /// to <see cref="IActiveDocumentChangeNotifier.ActiveDocumentChanged"/>.
    /// </summary>
    IProjectBaseActivator ProjectBaseActivator,
    /// <summary>
    /// Phase 30 / Issue #119: pure-C# evaluator used by the VM to compute the
    /// "active base matches current document" flag for gate commands. The VM
    /// uses it independently from <see cref="ProjectBaseActivator"/> (which
    /// drives the actual database switch) so that the CanLoad/CanPlace flags
    /// can be re-evaluated even when no switch happens (e.g. the current doc
    /// matches the existing active base).
    /// </summary>
    IProjectBaseBindingEvaluator ProjectBaseEvaluator,
    /// <summary>
    /// Database-update state (docs/architecture/database-migrations.md,
    /// Issue #126): shared singleton holding "update required / pending /
    /// running". The main VM refreshes it after init and on every database
    /// switch and maps it onto the badge/banner UI; write commands across
    /// the module gate through it (read-only database while pending).
    /// </summary>
    IDatabaseUpdateStateService UpdateState,
    /// <summary>
    /// ADR-058 (#173): plugin↔database forward-compatibility gate. Refreshed
    /// on connect/switch/init BEFORE the RBAC role resolution (the access
    /// service ANDs <see cref="IDatabaseCompatibilityService.IsDatabaseNewerThanPlugin"/>
    /// into every write-access decision).
    /// </summary>
    IDatabaseCompatibilityService CompatibilityService,
    /// <summary>
    /// ADR-058 (#173): opens the About dialog (update channel, changelog,
    /// update check) from the plugin-compatibility banner's
    /// "Обновить приложение" button.
    /// </summary>
    IAboutDialogService AboutDialogService,
    /// <summary>
    /// Import Validation Gate: resolves effective validation rules per
    /// category and evaluates batch-row snapshots (no .rfa re-open).
    /// Consumed by the batch import dialog's revalidation flow.
    /// </summary>
    IFamilyImportValidationService ValidationService,
    /// <summary>
    /// #241: evaluates the auto-assignment rule groups against batch-row
    /// snapshots — new families get their catalog category automatically
    /// in the batch import dialog.
    /// </summary>
    ICategoryAutoAssignService AutoAssignService,
    /// <summary>
    /// Import Validation Gate for category change inside the catalog
    /// (DnD in the tree, category picker in properties): blocks moves
    /// into rule-protected categories when the family fails the rules.
    /// </summary>
    ICategoryChangeGateService CategoryChangeGate,
    /// <summary>
    /// Issue #188: ES-based marker distinguishing SmartCon reference
    /// mini-projects from user work projects. Used by the post-import close
    /// (#186 — never close an unmarked document) and by the active-document
    /// notifier in the Revit layer (auto-DB-switch guard).
    /// </summary>
    IMiniProjectMarker MiniProjectMarker,
    /// <summary>
    /// Issue #187: system type finder for the project-presence badges on
    /// type nodes (one CollectTypes pass per tree load).
    /// </summary>
    ISystemTypeFinder SystemTypeFinder,
    /// <summary>
    /// ADR-066 (EPIC #207): parent→child dependency links between catalog
    /// items (<c>family_dependencies</c>, V29). Consumed by the batch-import
    /// executor to persist routing-fitting links after import (E1).
    /// </summary>
    IFamilyDependencyRepository FamilyDependencyRepository,
    /// <summary>
    /// Issue #249 (Phase 4): read access to the stored content analytics
    /// of catalog versions (section hashes + per-type hashes) — the batch
    /// dialog's "what changed" diff against the active version.
    /// </summary>
    IContentHashAnalyticsRepository ContentHashAnalytics);

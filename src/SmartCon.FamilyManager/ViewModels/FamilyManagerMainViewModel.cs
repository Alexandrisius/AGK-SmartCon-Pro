using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Common;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Events;
using SmartCon.FamilyManager.Selectors;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// ViewModel for the FamilyManager dockable panel.
/// </summary>
public sealed partial class FamilyManagerMainViewModel : ObservableObject, IDisposable
{
    private readonly IFamilyCatalogProvider _catalogProvider;
    private readonly IWritableFamilyCatalogProvider _writableProvider;
    private readonly IFamilyImportService _importService;
    private readonly IFamilyImportPrecomputer _importPrecomputer;
    private readonly StoragePathResolver _pathResolver;
    private readonly IFamilyFileResolver _fileResolver;
    private readonly IFamilyLoadService _loadService;
    private readonly IFamilyManagerDialogService _dialogService;
    private readonly IFamilyManagerAwaitableEvent _awaitableEvent;
    private readonly IFamilyManagerViewModelFactory _viewModelFactory;
    private readonly IRevitContext _revitContext;
    private readonly IDatabaseManager _databaseManager;
    private readonly ITransactionService _transactionService;
    private readonly ICategoryRepository _categoryRepository;
    private readonly IFamilyTypeRepository _typeRepository;
    private readonly IFamilyDataExtractionService _extractionService;
    private readonly IFamilyDataImportService _dataImportService;
    private readonly IDbAccessControlService _accessControl;
    private readonly IFamilySearchService _familySearchService;
    private readonly IFamilyPlacementService _familyPlacementService;
    private readonly IFamilyPlacementDragService _placementDragService;
    private readonly IRevitFileInfoReader _fileInfoReader;
    private readonly IFamilyAssetService _assetService;
    private readonly IFamilyMetadataExtractionService _metadataService;
    private readonly ISystemFamilyPlacementService _systemFamilyPlacementService;
    private readonly ISystemTypeSyncOrchestrator _systemSyncOrchestrator;
    private readonly ISystemFamilyRevitOperations _systemFamilyRevitOps;
    private readonly ISystemFamilyIsolationProjectService _systemFamilyIsolationProject;
    private readonly ISystemFamilyAttributeExtractor _systemFamilyAttributeExtractor;
    private readonly ISystemFamilyImportOrchestrator _systemFamilyImportOrchestrator;
    private readonly IActiveDocumentClassifier _activeDocumentClassifier;
    private readonly ILoadableFamilyScanner _loadableFamilyScanner;
    private readonly ILoadableFamilyImportOrchestrator _loadableFamilyImportOrchestrator;
    private readonly IStaleDetector _staleDetector;
    private readonly IStaleFamilyUpdater _staleUpdater;
    private readonly IStaleCategoryAggregator _staleAggregator;
    private readonly IFamilyFinder _familyFinder;
    private readonly IFamilyVersionWriter _versionWriter;
    private readonly IClock _clock;
    private readonly ISharedNestedFamilyRepository _sharedNestedRepository;
    private readonly IFamilyDependencyRepository _familyDependencyRepository;
    private readonly IFamilyRoutingRuleRepository _routingRuleRepository;
    private readonly ISegmentSizeRepository _segmentSizeRepository;
    private readonly ISegmentRuleRepository _segmentRuleRepository;
    private readonly IDispatcher _dispatcher;
    private readonly FamilyImportPreparationService _preparationService;
    private readonly IContentHashDedupService _dedupService;
    private readonly IUiFreezeRecoveryService _freezeRecovery;
    private readonly IActiveDocumentChangeNotifier _activeDocumentNotifier;
    private readonly IDatabaseUpdateStateService _updateState;
    private readonly IProjectBaseActivator _projectBaseActivator;
    private readonly IProjectBaseBindingEvaluator _projectBaseEvaluator;
    private readonly IDatabaseCompatibilityService _compatibility;
    private readonly IAboutDialogService _aboutDialogService;
    private readonly IFamilyImportValidationService _validationService;
    private readonly ICategoryChangeGateService _categoryChangeGate;
    private readonly ICategoryAutoAssignService _autoAssignService;
    private readonly IMiniProjectMarker _miniProjectMarker;
    private readonly ISystemTypeFinder _systemTypeFinder;
    /// <summary>#249 (Phase 4): stored content analytics for the batch
    /// dialog's "what changed" diff.</summary>
    private readonly IContentHashAnalyticsRepository _contentHashAnalytics;

    /// <summary>#259: catalog compliance check («Проверить → Правила») —
    /// session snapshot of rule verdicts, pure DB (no Revit).</summary>
    private readonly ICatalogComplianceService _complianceService;

    /// <summary>#133: routing-phantom detector (rules referencing families that left the catalog).</summary>
    private readonly IRoutingEditorService _routingEditorService;

    private string? _currentActiveDocumentPath;
    private bool _activeBaseCompatibleWithCurrentDoc = true;
    private ProjectBaseMatch? _activeBaseMatch;
    private CancellationTokenSource? _searchCts;
    private bool _suppressConnectionChanged;
    private CategoryNodeViewModel? _noCategoryNode;
    private bool _lastSearchActive;
    private bool _previousLoadWasSearch;
    private readonly HashSet<string> _savedExpandedCategoryIds = new();
    private readonly HashSet<string> _savedExpandedFamilyIds = new();
    /// <summary>#187 (M1): the tree was loaded at least once this session —
    /// gates the presence refresh on document switches without a DB switch
    /// (audit B4: replaces the write-only _lastTreeCatalogItems list).</summary>
    private bool _treeLoadedOnce;
    /// <summary>#187 (#2): cached presence snapshot of the active document —
    /// re-applied to freshly rebuilt tree nodes so badges do not flicker.</summary>
    private FamilyManagerMainViewModel.ProjectPresenceSnapshot? _presenceSnapshot;
    private bool _presenceRecomputeInFlight;
    private bool _presenceRecomputePending;

    // ── Stale detection session cache (Phase 24 / ADR-030) ─────────────
    [ObservableProperty] private bool _isStaleCheckInProgress;
    [ObservableProperty] private string? _staleCheckMessage;

    // ── Pane bottom progress bar: visual feed of the background mini-tasks
    // (post-import stale check, manual «Проверить», batch «Обновить») — the
    // ExternalEvent round-trips become visible instead of looking like a
    // phantom Revit freeze. The text stays in StaleCheckMessage (existing
    // binding); the bar only adds the determinate strip above the status line.
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMaximum = 1;
    [ObservableProperty] private bool _isProgressVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchNotEmpty))]
    private string _searchText = string.Empty;

    public bool IsSearchNotEmpty => !string.IsNullOrEmpty(SearchText);

    [ObservableProperty] private FamilyCatalogItemRow? _selectedItem;
    [ObservableProperty] private ObservableCollection<CatalogTreeNodeViewModel> _treeNodes = [];
    [ObservableProperty] private CatalogTreeNodeViewModel? _selectedTreeNode;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _totalItemCount;
    [ObservableProperty] private bool _canLoadToProject;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlaceTypeCommand))]
    private bool _canPlaceType;

    [ObservableProperty] private ObservableCollection<DatabaseListItem> _connections = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConvertSelectedToProject))]
    [NotifyPropertyChangedFor(nameof(CanConvertSelectedToGeneral))]
    [NotifyPropertyChangedFor(nameof(CanConfigureSelectedProjectBase))]
    [NotifyCanExecuteChangedFor(nameof(ConfigureProjectBaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConvertToProjectBaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConvertToGeneralBaseCommand))]
    private DatabaseListItem? _selectedConnection;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanupMissingRecordsCommand))]
    private bool _hasActiveDatabase;
    [ObservableProperty] private int _currentRevitVersion;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFileToCategoryCommand))]
    private bool _canImport;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCategoryEditorCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditFamilyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportActiveFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportSelectedElementsCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditSystemFamilyCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteFamilyCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartDragCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropFamilyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfigureProjectBaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConvertToProjectBaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConvertToGeneralBaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanupMissingRecordsCommand))]
    [NotifyPropertyChangedFor(nameof(CanConvertSelectedToProject))]
    [NotifyPropertyChangedFor(nameof(CanConvertSelectedToGeneral))]
    [NotifyPropertyChangedFor(nameof(CanConfigureSelectedProjectBase))]
    [NotifyPropertyChangedFor(nameof(HasAnyDatabaseUpdate))]
    [NotifyPropertyChangedFor(nameof(HasProcessableCriticalPending))]
    [NotifyPropertyChangedFor(nameof(DatabaseUpdateBannerText))]
    private bool _canEdit;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteDatabaseCommand))]
    private bool _canManageUsers;

    // ADR-058 (#173): the active database was upgraded by a newer SmartCon —
    // show the plugin-update banner and hide the DB-update banner/badge
    // (this plugin cannot act on them anyway).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PluginGateBannerText))]
    [NotifyPropertyChangedFor(nameof(ShowDatabaseUpdateBanner))]
    [NotifyPropertyChangedFor(nameof(HasDatabaseUpdateIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowPluginGateBanner))]
    private bool _isDatabaseNewerThanPlugin;

    // ADR-058 (#173): the compat banner targets write-capable roles only —
    // for an Engineer the gate changes nothing (the role is read-only by
    // definition), so the banner would be meaningless noise.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPluginGateBanner))]
    private bool _isEditorRole;

    public bool ShowPluginGateBanner => IsDatabaseNewerThanPlugin && IsEditorRole;

    public string PluginGateBannerText => string.Format(
        LanguageManager.GetString(StringLocalization.Keys.FM_PluginGate_BannerText)
            ?? "База данных обновлена до более новой версии SmartCon ({0}). Текущая версия приложения устарела: просмотр и загрузка семейств в проект доступны, но изменение базы недоступно. Обновите приложение, чтобы снять ограничение.",
        _compatibility.DatabaseMinPluginVersion);

    public bool ShowDatabaseUpdateBanner => IsDatabaseUpdateRequired && !IsDatabaseNewerThanPlugin;

    public FamilyManagerMainViewModel(FamilyManagerServices services)
    {
        Guard.ThrowIfNull(services);

        _catalogProvider = services.CatalogProvider;
        _writableProvider = services.WritableProvider;
        _importService = services.ImportService;
        _importPrecomputer = services.ImportPrecomputer;
        _pathResolver = services.PathResolver;
        _fileResolver = services.FileResolver;
        _loadService = services.LoadService;
        _dialogService = services.DialogService;
        _awaitableEvent = services.AwaitableEvent;
        _viewModelFactory = services.ViewModelFactory;
        _revitContext = services.RevitContext;
        _databaseManager = services.DatabaseManager;
        _transactionService = services.TransactionService;
        _categoryRepository = services.CategoryRepository;
        _typeRepository = services.TypeRepository;
        _extractionService = services.ExtractionService;
        _dataImportService = services.DataImportService;
        _accessControl = services.AccessControl;
        _familySearchService = services.FamilySearchService;
        _familyPlacementService = services.FamilyPlacementService;
        _placementDragService = services.PlacementDragService;
        _fileInfoReader = services.FileInfoReader;
        _assetService = services.AssetService;
        _metadataService = services.MetadataService;
        _systemFamilyPlacementService = services.SystemFamilyPlacementService;
        _systemSyncOrchestrator = services.SystemSyncOrchestrator;
        _systemFamilyRevitOps = services.SystemFamilyRevitOps;
        _systemFamilyIsolationProject = services.SystemFamilyIsolationProject;
        _systemFamilyAttributeExtractor = services.SystemFamilyAttributeExtractor;
        _systemFamilyImportOrchestrator = services.SystemFamilyImportOrchestrator;
        _activeDocumentClassifier = services.ActiveDocumentClassifier;
        _loadableFamilyScanner = services.LoadableFamilyScanner;
        _loadableFamilyImportOrchestrator = services.LoadableFamilyImportOrchestrator;
        _staleDetector = services.StaleDetector;
        _staleUpdater = services.StaleUpdater;
        _staleAggregator = services.StaleCategoryAggregator;
        _familyFinder = services.FamilyFinder;
        _versionWriter = services.VersionWriter;
        _clock = services.Clock;
        _sharedNestedRepository = services.SharedNestedRepository;
        _familyDependencyRepository = services.FamilyDependencyRepository;
        _routingRuleRepository = services.FamilyRoutingRuleRepository;
        _segmentSizeRepository = services.SegmentSizeRepository;
        _segmentRuleRepository = services.SegmentRuleRepository;

        // v2.0.0 (ADR-036, M-019-003): inject IDispatcher instead of capturing
        // Application.Current?.Dispatcher. The latter is null in net48 Revit
        // addins (WPF Application is not auto-created), and the fallback to
        // Dispatcher.CurrentDispatcher is unreliable from background threads.
        // WpfDispatcher (DI-registered) is net48-safe and unit-testable.
        _dispatcher = services.Dispatcher;
        SmartConLogger.Debug($"FamilyManagerMainViewModel.ctor: _dispatcher captured");
        _preparationService = services.PreparationService;
        _dedupService = services.DedupService;
        _freezeRecovery = services.FreezeRecovery;
        _activeDocumentNotifier = services.ActiveDocumentNotifier;
        _updateState = services.UpdateState;
        _projectBaseActivator = services.ProjectBaseActivator;
        _projectBaseEvaluator = services.ProjectBaseEvaluator;
        _compatibility = services.CompatibilityService;
        _aboutDialogService = services.AboutDialogService;
        _validationService = services.ValidationService;
        _categoryChangeGate = services.CategoryChangeGate;
        _autoAssignService = services.AutoAssignService;
        _miniProjectMarker = services.MiniProjectMarker;
        _systemTypeFinder = services.SystemTypeFinder;
        _contentHashAnalytics = services.ContentHashAnalytics;
        _complianceService = services.ComplianceService;
        _routingEditorService = services.RoutingEditorService;

        _updateState.StateChanged += OnDatabaseUpdateStateChanged;
        SyncDatabaseUpdateState();

        _databaseManager.ActiveDatabaseChanged += OnActiveDatabaseChanged;
        _activeDocumentNotifier.ActiveDocumentChanged += OnActiveDocumentChanged;
        _activeDocumentNotifier.ActiveDocumentPathChanged += OnActiveDocumentPathChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        _placementDragService.PlacementCompleted += OnPlacementCompleted;
        _placementDragService.SystemTypePlaced += OnSystemTypePlaced;
        _placementDragService.PlacementFailed += OnPlacementFailed;
        _placementDragService.PlacementSucceeded += OnPlacementSucceeded;
        _placementDragService.PlacementStatusMessage += OnPlacementStatusMessage;
        _placementDragService.SharedFamilyDecisionRequested += OnSharedFamilyDecisionRequested;

        DetectRevitVersion();
        FireAndForget(InitializeAsync(), nameof(InitializeAsync));
    }

    private void DetectRevitVersion()
    {
        try
        {
            var versionStr = _revitContext.GetRevitVersion();
            SmartConLogger.Debug($"DetectRevitVersion: GetRevitVersion() returned '{versionStr}' (len={versionStr?.Length ?? 0})");
            if (int.TryParse(versionStr, out var v))
            {
                CurrentRevitVersion = v;
                SmartConLogger.Debug($"DetectRevitVersion: parsed successfully → CurrentRevitVersion={v}");
            }
            else
            {
                SmartConLogger.Warn($"DetectRevitVersion: int.TryParse('{versionStr}') returned false — CurrentRevitVersion stays 0. [Action: check Application.VersionNumber format, consider InvariantCulture parse]");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"DetectRevitVersion failed: {ex.Message} [Action: restart Revit and verify the version matches one of supported R19/R21/R24/R25]");
        }
    }

    private void OnLanguageChanged()
    {
        var newLabel = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "No category";
        if (_noCategoryNode is not null)
        {
            _noCategoryNode.DisplayName = newLabel;
            _noCategoryNode.FullPath = newLabel;
        }
    }

    private DateTime _sessionStart = DateTime.MinValue;

    [RelayCommand(CanExecute = nameof(HasActiveDatabase))]
    private async Task OpenProfileAsync(CancellationToken ct)
    {
        try
        {
            var profileVm = _viewModelFactory.CreateProfileViewModel();
            await profileVm.InitializeAsync(ct);
            _dialogService.ShowProfile(profileVm);
        }
        catch (SmartCon.Core.Models.FamilyManager.DbAccessDeniedException)
        {
            return;
        }

        try
        {
            await _accessControl.RefreshCurrentUserAsync(ct);
            UpdateAccessProperties();
        }
        catch (SmartCon.Core.Models.FamilyManager.DbAccessDeniedException)
        {
        }
    }

    private SharedFamiliesLoadChoice OnSharedFamilyDecisionRequested(SharedFamilyDecisionRequest request)
    {
        return _dialogService.ShowSharedFamiliesLoadModeDialog(request);
    }

    public void Dispose()
    {
        using var _scope = SmartConLogger.BeginScope("FMVM",
            ("Method", "Dispose"));
        _databaseManager.ActiveDatabaseChanged -= OnActiveDatabaseChanged;
        _updateState.StateChanged -= OnDatabaseUpdateStateChanged;
        _activeDocumentNotifier.ActiveDocumentChanged -= OnActiveDocumentChanged;
        _activeDocumentNotifier.ActiveDocumentPathChanged -= OnActiveDocumentPathChanged;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        _placementDragService.PlacementCompleted -= OnPlacementCompleted;
        _placementDragService.SystemTypePlaced -= OnSystemTypePlaced;
        _placementDragService.PlacementFailed -= OnPlacementFailed;
        _placementDragService.PlacementSucceeded -= OnPlacementSucceeded;
        _placementDragService.PlacementStatusMessage -= OnPlacementStatusMessage;
        _placementDragService.SharedFamilyDecisionRequested -= OnSharedFamilyDecisionRequested;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        if (_sessionStart != DateTime.MinValue)
        {
            SmartConLogger.LogSessionEnd($"FamilyManager (Revit {CurrentRevitVersion})", _sessionStart);
        }
    }
}


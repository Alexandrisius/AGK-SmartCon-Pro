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

    private async Task InitializeAsync()
    {
        DumpLoadedAssembliesBeforeTruncate();
        SmartConLogger.TruncateMainLog();
        _sessionStart = DateTime.Now;

        await _databaseManager.InitializeAsync().ConfigureAwait(true);
        RecomputeActiveBaseMatch();
        RefreshConnections();
        if (!HasActiveDatabase)
        {
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_StatusNoDatabase) ?? "No database connected";
            return;
        }
        // The ExternalEvent round-trip wires up the Revit context; the
        // update-state check below depends on DetectRevitVersion, so it
        // must run after it, not fire-and-forget in parallel.
        await RefreshTreeViaExternalEventAsync().ConfigureAwait(true);
        // Issue #126: detect stale (v1) content hashes — shows the red
        // badge + "Update database" command; never pops a dialog.
        await RefreshDatabaseUpdateStateAsync().ConfigureAwait(true);
    }

    private static void DumpLoadedAssembliesBeforeTruncate()
    {
        try
        {
            var appDir = Path.GetDirectoryName(typeof(FamilyManagerMainViewModel).Assembly.Location);
            var asmLogPath = Path.Combine(appDir ?? ".", "assembly-load.log");
            var sb = new StringBuilder();
            sb.AppendLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] === PRE-TRUNCATE DUMP: all currently-loaded HelixToolkit/SharpDX/Assimp assemblies ===");
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = a.GetName().Name ?? "";
                if (n.Contains("HelixToolkit") || n.Contains("SharpDX") || n.Contains("Assimp") ||
                    n.Contains("SmartCon"))
                    sb.AppendLine($"  {n} v{a.GetName().Version} from={a.Location}");
            }
            sb.AppendLine(new string('=', 80));
            File.AppendAllText(asmLogPath, sb.ToString());
        }
        catch { }
    }

    private static void FireAndForget(Task task, string operationName)
    {
        Guard.ThrowIfNull(task);
        _ = task.ContinueWith(
            t => SmartConLogger.Error($"FamilyManager '{operationName}' failed: {t.Exception?.GetBaseException()}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Fire-and-forget helper for inline async lambdas. Schedules the factory on the
    /// thread pool so the calling Revit UI thread is never blocked, and routes any
    /// exception through the operation-scoped log without leaking as
    /// <c>AppDomain.UnhandledException</c>. Prefer
    /// <see cref="FireAndForget(Task, string)"/> when the task is already constructed.
    /// </summary>
    private static void FireAndForget(Func<Task> taskFactory, string operationName)
    {
        Guard.ThrowIfNull(taskFactory);
        _ = Task.Run(async () =>
        {
            try
            {
                await taskFactory().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SmartConLogger.Error($"FireAndForget '{operationName}' failed: {ex.GetBaseException()}");
            }
        });
    }

    private async Task RefreshAccessAndLoadTreeAsync()
    {
        DetectRevitVersion();
        SmartConLogger.LogSessionStart($"FamilyManager (Revit {CurrentRevitVersion})");

        if (!HasActiveDatabase)
        {
            _compatibility.Reset();
            IsDatabaseNewerThanPlugin = false;
            IsEditorRole = false;
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_StatusNoDatabase) ?? "No database connected";
            TreeNodes = new ObservableCollection<CatalogTreeNodeViewModel>();
            CanImport = false;
            CanEdit = false;
            CanManageUsers = false;
            return;
        }

        // ADR-058 (#173): refresh the compat gate BEFORE the role resolution —
        // ApplyWriteAccess ANDs IsDatabaseNewerThanPlugin into write access.
        await _compatibility.RefreshAsync();
        IsDatabaseNewerThanPlugin = _compatibility.IsDatabaseNewerThanPlugin;

        _accessControl.InvalidateCache();

        try
        {
            await _accessControl.RefreshCurrentUserAsync();
            UpdateAccessProperties();
        }
        catch (DbAccessDeniedException ex)
        {
            CanImport = false;
            CanEdit = false;
            CanManageUsers = false;
            IsEditorRole = false;
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_AccessDenied) ?? "Access Denied",
                string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_AccessDeniedMessage) ?? "The owner of \"{0}\" has restricted your access.", ex.DbName));
            TreeNodes = new ObservableCollection<CatalogTreeNodeViewModel>();
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_AccessDenied) ?? "Access Denied";
            return;
        }

        await LoadTreeAsync();
    }

    private void UpdateAccessProperties()
    {
        // Write capabilities come pre-gated from the service: CanImport/
        // CanEdit/CanManageUsers already AND the plugin-compat gate (ADR-058,
        // central enforcement point in DbAccessControlService).
        CanImport = _accessControl.CanImport;
        CanEdit = _accessControl.CanEdit;
        CanManageUsers = _accessControl.CanManageUsers;
        IsEditorRole = _accessControl.IsEditorRole;
    }

    partial void OnSearchTextChanged(string value)
    {
        var isSearchNow = !string.IsNullOrWhiteSpace(value);
        var savedCatCount = _savedExpandedCategoryIds.Count;
        var savedFamCount = _savedExpandedFamilyIds.Count;

        if (isSearchNow && !_lastSearchActive)
        {
            _savedExpandedCategoryIds.Clear();
            _savedExpandedFamilyIds.Clear();
            CollectExpandedIds(TreeNodes, _savedExpandedCategoryIds, _savedExpandedFamilyIds);
        }

        _lastSearchActive = isSearchNow;

        // DIAG-DUMP (Issue: net48 tree-expand after search).
        // Tracks the lifecycle of the search box so we can correlate the user
        // typing a term with the eventual TreeViewItem.IsExpanded state.
        // Without this, the search logic in OnSearchTextChanged → DebouncedSearchAsync
        // → LoadTreeAsync is invisible in the log.
        SmartConLogger.Info(
            $"FMTree.SearchTextChanged: newValue='{value}' isSearch={isSearchNow} " +
            $"prevSearchActive={!isSearchNow != _lastSearchActive} " +
            $"savedCats={savedCatCount} savedFams={savedFamCount} " +
            $"treeNodesBefore={TreeNodes.Count}");

        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _searchCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();
        _ = DebouncedSearchAsync(newCts.Token);
    }

    private async Task DebouncedSearchAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);
            // DIAG-DUMP: search debounce elapsed, now triggering LoadTreeAsync
            SmartConLogger.Debug(
                $"FMTree.DebouncedSearch: 300ms elapsed, calling LoadTreeAsync. " +
                $"thread={Environment.CurrentManagedThreadId} syncCtx={SynchronizationContext.Current?.GetType().Name ?? "<none>"}");
            await LoadTreeAsync(ct);
        }
        catch (OperationCanceledException)
        {
            SmartConLogger.Debug(
                $"FMTree.DebouncedSearch: cancelled (newer keystroke took over)");
        }
    }

    partial void OnSelectedItemChanged(FamilyCatalogItemRow? value)
    {
        RefreshCanLoadToProject();
    }

    partial void OnSelectedTreeNodeChanged(CatalogTreeNodeViewModel? value)
    {
        if (value is FamilyLeafNodeViewModel leaf)
        {
            SelectedItem = new FamilyCatalogItemRow
            {
                Id = leaf.CatalogItemId,
                Name = leaf.DisplayName,
                CategoryId = leaf.CategoryId,
                CategoryName = leaf.CategoryPath,
                Manufacturer = leaf.Manufacturer,
                ContentStatus = leaf.ContentStatus,
                VersionLabel = leaf.VersionLabel,
                UpdatedAtUtc = leaf.UpdatedAtUtc,
                Tags = leaf.Tags,
                Description = leaf.Description,
                RevitCategory = leaf.RevitCategory,
                ActiveRevitMajorVersion = leaf.ActiveRevitMajorVersion,
                MinRevitMajorVersion = leaf.MinRevitMajorVersion,
            };
        }
        else if (value is FamilyTypeNodeViewModel typeNode)
        {
            var parent = FindParentOf(TreeNodes, typeNode);
            if (parent is FamilyLeafNodeViewModel parentLeaf)
            {
                SelectedItem = new FamilyCatalogItemRow
                {
                    Id = parentLeaf.CatalogItemId,
                    Name = parentLeaf.DisplayName,
                    CategoryId = parentLeaf.CategoryId,
                    CategoryName = parentLeaf.CategoryPath,
                    Manufacturer = parentLeaf.Manufacturer,
                    ContentStatus = parentLeaf.ContentStatus,
                    VersionLabel = parentLeaf.VersionLabel,
                    UpdatedAtUtc = parentLeaf.UpdatedAtUtc,
                    Tags = parentLeaf.Tags,
                    Description = parentLeaf.Description,
                    RevitCategory = parentLeaf.RevitCategory,
                    ActiveRevitMajorVersion = parentLeaf.ActiveRevitMajorVersion,
                    MinRevitMajorVersion = parentLeaf.MinRevitMajorVersion,
                };
            }
            else
            {
                SelectedItem = null;
            }
        }
        else
        {
            SelectedItem = null;
        }

        RefreshCanPlaceType();
        StartPlacementDragCommand.NotifyCanExecuteChanged();
        ImportFileToCategoryCommand.NotifyCanExecuteChanged();
    }

    private bool IsSelectedItemRevitIncompatible()
        => SelectedItem is not null
            && SelectedItem.ActiveRevitMajorVersion.HasValue
            && CurrentRevitVersion > 0
            && SelectedItem.ActiveRevitMajorVersion.Value > CurrentRevitVersion;

    private void RefreshCanLoadToProject()
    {
        CanLoadToProject = SelectedItem is not null
            && SelectedItem.ContentStatus == ContentStatus.Active
            && !IsSelectedItemRevitIncompatible()
            && _accessControl.CanLoadToProject
            && _activeBaseCompatibleWithCurrentDoc;
        LoadToProjectCommand.NotifyCanExecuteChanged();
        LoadToProjectKeepParamsCommand.NotifyCanExecuteChanged();
    }

    private void RefreshCanPlaceType()
    {
        if (SelectedTreeNode is FamilyTypeNodeViewModel typeNode)
        {
            var parent = FindParentOf(TreeNodes, typeNode);
            if (parent is FamilyLeafNodeViewModel parentLeaf)
            {
                // #187: menu/command agreement — "Разместить" is hidden for a
                // stale type (only "Обновить" is offered), so CanExecute must
                // agree.
                CanPlaceType = typeNode.PresenceState != TypePresenceState.StaleInProject
                    && CanLoadLeafToProject(parentLeaf);
            }
            else
            {
                CanPlaceType = false;
            }
        }
        else
        {
            CanPlaceType = false;
        }

        PlaceTypeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void OnTreeViewSelectedItemChanged(object? selectedItem)
    {
        SelectedTreeNode = selectedItem as CatalogTreeNodeViewModel;
    }

    internal static CatalogTreeNodeViewModel? FindParentOf(ObservableCollection<CatalogTreeNodeViewModel> nodes, CatalogTreeNodeViewModel target)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Contains(target)) return node;
            var found = FindParentOf(node.Children, target);
            if (found is not null) return found;
        }
        return null;
    }

    internal static int CountFamiliesRecursive(CatalogTreeNodeViewModel node)
    {
        var count = 0;
        foreach (var child in node.Children)
        {
            if (child is FamilyLeafNodeViewModel)
                count++;
            else
                count += CountFamiliesRecursive(child);
        }
        return count;
    }

    internal static void CollectExpandedIds(ObservableCollection<CatalogTreeNodeViewModel>? nodes, HashSet<string> catIds, HashSet<string>? familyIds)
    {
        if (nodes is null) return;
        foreach (var node in nodes)
        {
            if (node.IsExpanded)
            {
                if (node is CategoryNodeViewModel cat) catIds.Add(cat.CategoryId);
                else if (node is FamilyLeafNodeViewModel leaf && familyIds is not null) familyIds.Add(leaf.CatalogItemId);
            }
            CollectExpandedIds(node.Children, catIds, familyIds);
        }
    }

    internal static void CollectFamilyIds(ObservableCollection<CatalogTreeNodeViewModel> nodes, List<string> ids)
    {
        foreach (var node in nodes)
        {
            if (node is FamilyLeafNodeViewModel leaf)
                ids.Add(leaf.CatalogItemId);
            CollectFamilyIds(node.Children, ids);
        }
    }

    internal static void AttachTypesToNodes(ObservableCollection<CatalogTreeNodeViewModel> nodes, IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>> batch, HashSet<string> expandedFamilyIds)
    {
        foreach (var node in nodes)
        {
            if (node is FamilyLeafNodeViewModel leaf)
            {
                if (batch.TryGetValue(leaf.CatalogItemId, out var types))
                {
                    foreach (var t in types)
                    {
                        // #172: the synthetic <default> row exists in family_types
                        // only for content-hash and attribute stability (ADR-049/056).
                        // In the tree it must behave like the v2.0.0 virtual node
                        // (family name, IsVirtual) — otherwise Place/DnD would send
                        // the raw "<default>" literal to LoadFamilySymbol and fail,
                        // and the user would see the marker instead of the family name.
                        if (t.Name == FamilyTypeSnapshot.DefaultTypeName)
                        {
                            AddVirtualTypeNode(leaf);
                            continue;
                        }

                        leaf.Children.Add(new FamilyTypeNodeViewModel(
                            t.CatalogItemId, t.Name, isVirtual: false,
                            familySource: leaf.FamilySource, uniqueId: t.UniqueId,
                            displayName: FamilyTypeSnapshot.ResolveDisplayName(t.Name, leaf.DisplayName),
                            isUnavailable: leaf.IsUnavailable,
                            familyName: t.FamilyName,
                            familyKey: t.FamilyKey));
                    }
                }

                if (leaf.Children.Count == 0)
                {
                    AddVirtualTypeNode(leaf);
                }

                if (expandedFamilyIds.Contains(leaf.CatalogItemId))
                    leaf.IsExpanded = true;
            }
            AttachTypesToNodes(node.Children, batch, expandedFamilyIds);
        }
    }

    private static void AddVirtualTypeNode(FamilyLeafNodeViewModel leaf)
    {
        leaf.Children.Add(new FamilyTypeNodeViewModel(
            leaf.CatalogItemId, leaf.DisplayName, isVirtual: true,
            familySource: leaf.FamilySource, isUnavailable: leaf.IsUnavailable));
    }

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

    /// <summary>
    /// Triggers tree refresh via ExternalEvent. The RaiseAsync round-trip is
    /// LOAD-BEARING, not a warm-up (regression 2026-08-13): the first action
    /// processed by the AwaitableEvent wires <see cref="IRevitContext"/>
    /// (<c>ProcessQueue → IRevitContextWriter.SetContext</c> — Revit version,
    /// username). <c>RefreshAccessAndLoadTreeAsync</c> below calls
    /// <c>DetectRevitVersion()</c>, so the round-trip MUST complete before the
    /// dispatcher runs it — without it <c>CurrentRevitVersion</c> stays 0 for
    /// the whole session and every version-gated operation
    /// (<c>ResolveForLoadAsync</c>: «Загрузить в проект», «Редактировать»)
    /// fails with "No compatible version found".
    /// </summary>
    private async Task RefreshTreeViaExternalEventAsync()
    {
        try
        {
            await _awaitableEvent.RaiseAsync(_ => { }).ConfigureAwait(true);

            IsLoading = true;
            try
            {
                // v2.0.0 (ADR-036): IDispatcher.InvokeAsync takes an Action, but
                // RefreshTreeOnUiThreadAsync returns Task. Wrapping the async
                // lambda directly would generate an async void state machine,
                // which violates I-13 (exception swallowing) and breaks
                // testability. Extracting the async work into a named Task-
                // returning method and dispatching a fire-and-forget Action
                // (via `_ =`) keeps the Action synchronous and the exception
                // path observable through RefreshTreeOnUiThreadAsync itself.
                _ = _dispatcher.InvokeAsync(() => { _ = RefreshTreeOnUiThreadAsync(); });
            }
            finally
            {
                IsLoading = false;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"RefreshTreeViaExternalEvent failed: {ex.Message} [Action: click Refresh to retry, check smartcon.log]");
        }
    }

    private async Task RefreshTreeOnUiThreadAsync()
    {
        try
        {
            await RefreshAccessAndLoadTreeAsync();
        }
        catch (DbAccessDeniedException ex)
        {
            CanImport = false;
            CanEdit = false;
            CanManageUsers = false;
            IsEditorRole = false;
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_AccessDenied) ?? "Access Denied",
                string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_AccessDeniedMessage) ?? "The owner of \"{0}\" has restricted your access.", ex.DbName));
            TreeNodes = new ObservableCollection<CatalogTreeNodeViewModel>();
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_AccessDenied) ?? "Access Denied";
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"RefreshTreeAsync failed: {ex.Message} [Action: click Refresh to retry, check smartcon.log]");
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ErrorFormat) ?? "Error: {0}",
                ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshTree()
    {
        await RefreshTreeViaExternalEventAsync();
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    private void OnSystemTypePlaced(FamilyPlacementDragData data)
    {
        try
        {
            // Fired on the Revit main thread from the drop handler — hop to
            // the UI thread for the async badge re-eval (same pattern as
            // OnPlacementCompleted). Not awaited: UI refresh is opportunistic.
            _ = _dispatcher.InvokeAsync(() =>
            {
                _ = RefreshAfterSystemTypePlacedAsync(data);
            });
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"OnSystemTypePlaced dispatcher invoke failed: {ex.Message} [Action: run «Проверить» to refresh the stale badges]");
        }
    }

    /// <summary>
    /// ADR-066 follow-up: the DnD tail mirrors the load-to-project tail
    /// (LoadToProject → badge re-eval + full presence recompute). The sync
    /// behind a system type placement loads the type's routing fitting
    /// DEPENDENCIES into the project implicitly — only a full presence pass
    /// turns those loadable badges blue; the per-type badge re-eval alone
    /// covers just the placed parent type.
    /// </summary>
    private async Task RefreshAfterSystemTypePlacedAsync(FamilyPlacementDragData data)
    {
        await ReevaluateSystemItemBadgeAsync(
            data.CatalogItemId, data.TypeName, data.SystemFamilyName, data.SystemFamilyKey)
            .ConfigureAwait(true);
        await RefreshSystemTypeProjectPresenceSafeAsync().ConfigureAwait(true);
    }

    private void OnPlacementCompleted()
    {
        try
        {
            // v2.0.0 (ADR-036, M-019-003): IDispatcher.InvokeAsync returns Task.
            // We don't await here because OnPlacementCompleted is sync and the
            // caller is the placement event handler; UI refresh is opportunistic.
            _ = _dispatcher.InvokeAsync(() => { _ = LoadTreeAsync(); });
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"OnPlacementCompleted dispatcher invoke failed: {ex.Message} [Action: click Refresh to update the tree]");
        }
    }

    private void OnPlacementFailed(string errorMessage)
    {
        SmartConLogger.Warn($"{errorMessage} [Action: verify the family is loaded and the type exists, then click Refresh]");
        if (!SetStatusOnUiThread(errorMessage))
        {
            return;
        }
    }

    private void OnPlacementSucceeded(string successMessage)
    {
        if (!SetStatusOnUiThread(successMessage))
        {
            return;
        }
        SmartConLogger.Info($"{successMessage}");
    }

    private void OnPlacementStatusMessage(string statusMessage)
    {
        if (!SetStatusOnUiThread(statusMessage))
        {
            return;
        }
        SmartConLogger.Info($"{statusMessage}");
    }

    /// <summary>
    /// Defensive marshaling: these handlers are currently invoked on the Revit
    /// UI thread by <c>FamilyPlacementDropHandler</c>, but if a future refactor
    /// moves them to a background thread the unguarded <c>StatusMessage</c>
    /// setter would raise <c>PropertyChanged</c> on the wrong thread and
    /// freeze the WPF DockablePane (see <c>revit-api-best-practice</c> skill).
    /// </summary>
    private bool SetStatusOnUiThread(string message)
    {
        if (_dispatcher.CheckAccess())
        {
            StatusMessage = message;
        }
        else
        {
            _ = _dispatcher.InvokeAsync(() => StatusMessage = message);
        }
        return true;
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


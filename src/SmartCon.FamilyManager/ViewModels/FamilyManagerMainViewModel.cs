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
    private readonly IFamilyMetadataExtractionService _metadataService;
    private readonly ISystemFamilyPlacementService _systemFamilyPlacementService;
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
    private readonly IDispatcher _dispatcher;
    private readonly FamilyImportPreparationService _preparationService;
    private readonly IContentHashDedupService _dedupService;
    private readonly IUiFreezeRecoveryService _freezeRecovery;
    private CancellationTokenSource? _searchCts;
    private bool _suppressConnectionChanged;
    private CategoryNodeViewModel? _noCategoryNode;
    private bool _lastSearchActive;
    private readonly HashSet<string> _savedExpandedCategoryIds = new();
    private readonly HashSet<string> _savedExpandedFamilyIds = new();
    private HashSet<string>? _loadedFamilyNamesCache;
    private string? _loadedFamilyNamesCacheProjectPath;

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
    private string? _cachedProjectPath;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _totalItemCount;
    [ObservableProperty] private bool _canLoadToProject;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlaceTypeCommand))]
    private bool _canPlaceType;

    [ObservableProperty] private ObservableCollection<DatabaseConnection> _connections = new();
    [ObservableProperty] private DatabaseConnection? _selectedConnection;
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
    private bool _canEdit;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteDatabaseCommand))]
    private bool _canManageUsers;

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
        _metadataService = services.MetadataService;
        _systemFamilyPlacementService = services.SystemFamilyPlacementService;
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

        _databaseManager.ActiveDatabaseChanged += OnActiveDatabaseChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        _placementDragService.PlacementCompleted += OnPlacementCompleted;
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
                SmartConLogger.Warn($"DetectRevitVersion: int.TryParse('{versionStr}') returned false — CurrentRevitVersion stays 0. [Action: check Application.VersionNumber format, may need InvariantCulture parse]");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"DetectRevitVersion failed: {ex.Message} [Action: перезапустите Revit, проверьте что версия Revit соответствует одной из поддерживаемых R19/R21/R24/R25]");
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
        RefreshConnections();
        if (!HasActiveDatabase)
        {
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_StatusNoDatabase) ?? "No database connected";
            return;
        }
        _ = RefreshTreeViaExternalEventAsync();
    }

    private static void DumpLoadedAssembliesBeforeTruncate()
    {
        try
        {
            var appDir = Path.GetDirectoryName(typeof(FamilyManagerMainViewModel).Assembly.Location);
            var asmLogPath = Path.Combine(appDir ?? ".", "assembly-load.log");
            var sb = new StringBuilder();
            sb.AppendLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] === PRE-TRUNCATE DUMP: all currently-loaded HelixToolkit/SharpDX/SharpGLTF/Assimp assemblies ===");
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = a.GetName().Name ?? "";
                if (n.Contains("HelixToolkit") || n.Contains("SharpGLTF") ||
                    n.Contains("SharpDX") || n.Contains("Assimp") ||
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
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_StatusNoDatabase) ?? "No database connected";
            TreeNodes = new ObservableCollection<CatalogTreeNodeViewModel>();
            CanImport = false;
            CanEdit = false;
            CanManageUsers = false;
            return;
        }

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
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_AccessDenied) ?? "Access Denied",
                string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_AccessDeniedMessage) ?? "The owner of \"{0}\" has restricted your access.", ex.DbName));
            TreeNodes = new ObservableCollection<CatalogTreeNodeViewModel>();
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_AccessDenied) ?? "Access Denied";
            return;
        }

        await LoadTreeAsync();
    }

    /// <summary>
    /// Returns the set of family names currently loaded in the Revit document.
    /// The result is cached per project path and invalidated after family load/place operations.
    /// </summary>
    private HashSet<string> GetLoadedFamilyNamesCached()
    {
        var currentPath = _cachedProjectPath;
        if (_loadedFamilyNamesCache is not null &&
            _loadedFamilyNamesCacheProjectPath == currentPath)
        {
            return _loadedFamilyNamesCache;
        }

        var names = new HashSet<string>(_familySearchService.GetAllLoadedFamilyNames());
        _loadedFamilyNamesCache = names;
        _loadedFamilyNamesCacheProjectPath = currentPath;
        return names;
    }

    private void InvalidateLoadedFamilyNamesCache()
    {
        _loadedFamilyNamesCache = null;
        _loadedFamilyNamesCacheProjectPath = null;
    }

    private void UpdateAccessProperties()
    {
        CanImport = _accessControl.CanImport;
        CanEdit = _accessControl.CanEdit;
        CanManageUsers = _accessControl.CanManageUsers;
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
        CanLoadToProject = value is not null && value.ContentStatus == ContentStatus.Active && _accessControl.CanLoadToProject;
        LoadToProjectCommand.NotifyCanExecuteChanged();
        LoadToProjectKeepParamsCommand.NotifyCanExecuteChanged();

        if (value is not null)
        {
            // Place command removed - type-centric workflow
        }
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
            };
            CanPlaceType = false;
            LoadToProjectKeepParamsCommand.NotifyCanExecuteChanged();
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
                };
                CanPlaceType = parentLeaf.ContentStatus == ContentStatus.Active && _accessControl.CanLoadToProject;
                PlaceTypeCommand.NotifyCanExecuteChanged();
                LoadToProjectKeepParamsCommand.NotifyCanExecuteChanged();
            }
            else
            {
                SelectedItem = null;
                CanPlaceType = false;
            }
        }
        else
        {
            SelectedItem = null;
            CanPlaceType = false;
        }

        LoadToProjectCommand.NotifyCanExecuteChanged();
        LoadToProjectKeepParamsCommand.NotifyCanExecuteChanged();
        PlaceTypeCommand.NotifyCanExecuteChanged();
        StartPlacementDragCommand.NotifyCanExecuteChanged();
        ImportFileToCategoryCommand.NotifyCanExecuteChanged();
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

    private static void CollectExpandedIds(ObservableCollection<CatalogTreeNodeViewModel>? nodes, HashSet<string> catIds, HashSet<string>? familyIds)
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

    private static void AttachTypesToNodes(ObservableCollection<CatalogTreeNodeViewModel> nodes, IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>> batch, HashSet<string> expandedFamilyIds)
    {
        foreach (var node in nodes)
        {
            if (node is FamilyLeafNodeViewModel leaf)
            {
                if (batch.TryGetValue(leaf.CatalogItemId, out var types))
                {
                    foreach (var t in types)
                    {
                        leaf.Children.Add(new FamilyTypeNodeViewModel(
                            t.CatalogItemId, t.Name, isVirtual: false,
                            familySource: leaf.FamilySource, uniqueId: t.UniqueId));
                    }
                }

                if (leaf.Children.Count == 0)
                {
                    leaf.Children.Add(new FamilyTypeNodeViewModel(
                        leaf.CatalogItemId, leaf.DisplayName, isVirtual: true,
                        familySource: leaf.FamilySource));
                }

                if (expandedFamilyIds.Contains(leaf.CatalogItemId))
                    leaf.IsExpanded = true;
            }
            AttachTypesToNodes(node.Children, batch, expandedFamilyIds);
        }
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
    /// Triggers tree refresh via ExternalEvent so that Revit API (FilteredElementCollector)
    /// runs in the correct thread context before LoadTreeAsync builds the UI.
    /// </summary>
    private async Task RefreshTreeViaExternalEventAsync()
    {
        try
        {
            await _awaitableEvent.RaiseAsync(_ =>
            {
                try
                {
                    _cachedProjectPath = _revitContext.GetDocument().PathName;
                }
                catch
                {
                    _cachedProjectPath = null;
                }

                try
                {
                    GetLoadedFamilyNamesCached();
                }
                catch { }
            });

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
            SmartConLogger.Error($"RefreshTreeViaExternalEvent failed: {ex.Message} [Action: нажмите Refresh чтобы повторить, проверьте логи smartcon.log]");
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
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_AccessDenied) ?? "Access Denied",
                string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_AccessDeniedMessage) ?? "The owner of \"{0}\" has restricted your access.", ex.DbName));
            TreeNodes = new ObservableCollection<CatalogTreeNodeViewModel>();
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_AccessDenied) ?? "Access Denied";
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"RefreshTreeAsync failed: {ex.Message} [Action: нажмите Refresh чтобы повторить, проверьте логи smartcon.log]");
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

    private void OnPlacementCompleted()
    {
        try
        {
            InvalidateLoadedFamilyNamesCache();

            // v2.0.0 (ADR-036, M-019-003): IDispatcher.InvokeAsync returns Task.
            // We don't await here because OnPlacementCompleted is sync and the
            // caller is the placement event handler; UI refresh is opportunistic.
            _ = _dispatcher.InvokeAsync(() => { _ = LoadTreeAsync(); });
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"OnPlacementCompleted dispatcher invoke failed: {ex.Message} [Action: нажмите Refresh чтобы обновить дерево]");
        }
    }

    private void OnPlacementFailed(string errorMessage)
    {
        if (!SetStatusOnUiThread($"{errorMessage} [Action: проверьте, что семейство загружено в проект и тип существует; попробуйте Refresh]"))
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
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        _placementDragService.PlacementCompleted -= OnPlacementCompleted;
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


using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Events;
using SmartCon.FamilyManager.Services;
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
    private readonly IFamilyFileResolver _fileResolver;
    private readonly IFamilyLoadService _loadService;
    private readonly IProjectFamilyUsageRepository _usageRepo;
    private readonly IFamilyManagerDialogService _dialogService;
    private readonly IFamilyManagerExternalEvent _externalEvent;
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
    private CancellationTokenSource? _searchCts;
    private bool _suppressConnectionChanged;
    private CategoryNodeViewModel? _noCategoryNode;
    private bool _lastSearchActive;
    private readonly HashSet<string> _savedExpandedCategoryIds = new();
    private readonly HashSet<string> _savedExpandedFamilyIds = new();
    private HashSet<string>? _loadedFamilyNamesCache;
    private string? _loadedFamilyNamesCacheProjectPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchNotEmpty))]
    private string _searchText = string.Empty;

    public bool IsSearchNotEmpty => !string.IsNullOrEmpty(SearchText);

    [ObservableProperty] private FamilyCatalogItemRow? _selectedItem;
    [ObservableProperty] private ObservableCollection<CatalogTreeNodeViewModel> _treeNodes = [];
    [ObservableProperty] private CatalogTreeNodeViewModel? _selectedTreeNode;
    [ObservableProperty] private bool _isSelectedFamilyStale;
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
    [NotifyCanExecuteChangedFor(nameof(LoadActiveFamilyCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteFamilyCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartDragCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropFamilyCommand))]
    private bool _canEdit;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteDatabaseCommand))]
    private bool _canManageUsers;

    public FamilyManagerMainViewModel(
        IFamilyCatalogProvider catalogProvider,
        IWritableFamilyCatalogProvider writableProvider,
        IFamilyImportService importService,
        IFamilyFileResolver fileResolver,
        IFamilyLoadService loadService,
        IProjectFamilyUsageRepository usageRepo,
        IFamilyManagerDialogService dialogService,
        IFamilyManagerExternalEvent externalEvent,
        IFamilyManagerViewModelFactory viewModelFactory,
        IRevitContext revitContext,
        IDatabaseManager databaseManager,
        ITransactionService transactionService,
        ICategoryRepository categoryRepository,
        IFamilyTypeRepository typeRepository,
        IFamilyDataExtractionService extractionService,
        IFamilyDataImportService dataImportService,
        IDbAccessControlService accessControl,
        IFamilySearchService familySearchService,
        IFamilyPlacementService familyPlacementService,
        IFamilyPlacementDragService placementDragService,
        IRevitFileInfoReader fileInfoReader,
        IFamilyMetadataExtractionService metadataService)
    {
        _catalogProvider = catalogProvider;
        _writableProvider = writableProvider;
        _importService = importService;
        _fileResolver = fileResolver;
        _loadService = loadService;
        _usageRepo = usageRepo;
        _dialogService = dialogService;
        _externalEvent = externalEvent;
        _viewModelFactory = viewModelFactory;
        _revitContext = revitContext;
        _databaseManager = databaseManager;
        _transactionService = transactionService;
        _categoryRepository = categoryRepository;
        _typeRepository = typeRepository;
        _extractionService = extractionService;
        _dataImportService = dataImportService;
        _accessControl = accessControl;
        _familySearchService = familySearchService;
        _familyPlacementService = familyPlacementService;
        _placementDragService = placementDragService;
        _fileInfoReader = fileInfoReader;
        _metadataService = metadataService;

        _databaseManager.ActiveDatabaseChanged += OnActiveDatabaseChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        _placementDragService.PlacementCompleted += OnPlacementCompleted;
        _placementDragService.PlacementFailed += OnPlacementFailed;
        _placementDragService.PlacementSucceeded += OnPlacementSucceeded;
        _placementDragService.PlacementStatusMessage += OnPlacementStatusMessage;

        DetectRevitVersion();
        InitializeAsync();
    }

    private void DetectRevitVersion()
    {
        try
        {
            var versionStr = _revitContext.GetRevitVersion();
            if (int.TryParse(versionStr, out var v))
            {
                CurrentRevitVersion = v;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"DetectRevitVersion failed: {ex.Message}");
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

    private void InitializeAsync()
    {
        try
        {
            SmartConLogger.TruncateMainLog();
            SmartConLogger.Info($"======================================================================");
            SmartConLogger.Info($"FamilyManager SESSION START  Revit {CurrentRevitVersion}  [{DateTime.Now:yyyy-MM-dd HH:mm:ss}]");
            SmartConLogger.Info($"======================================================================");

            _databaseManager.InitializeAsync().GetAwaiter().GetResult();
            RefreshConnections();
            if (!HasActiveDatabase)
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_StatusNoDatabase) ?? "No database connected";
                return;
            }
            RefreshTreeViaExternalEvent();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"FamilyManager initialization failed: {ex}");
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Initialization error: {0}",
                ex.Message);
        }
    }

    private async Task RefreshAccessAndLoadTreeAsync()
    {
        if (!HasActiveDatabase)
        {
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_StatusNoDatabase) ?? "No database connected";
            TreeNodes = new ObservableCollection<CatalogTreeNodeViewModel>();
            CanImport = false;
            CanEdit = false;
            CanManageUsers = false;
            return;
        }

        DetectRevitVersion();
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

        if (isSearchNow && !_lastSearchActive)
        {
            _savedExpandedCategoryIds.Clear();
            _savedExpandedFamilyIds.Clear();
            CollectExpandedIds(TreeNodes, _savedExpandedCategoryIds, _savedExpandedFamilyIds);
        }

        _lastSearchActive = isSearchNow;

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
            await LoadTreeAsync(ct);
        }
        catch (OperationCanceledException)
        {
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
                IsSelectedFamilyStale = leaf.IsStale;
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
                    IsSelectedFamilyStale = parentLeaf.IsStale;
                    CanPlaceType = parentLeaf.ContentStatus == ContentStatus.Active && _accessControl.CanLoadToProject;
                    PlaceTypeCommand.NotifyCanExecuteChanged();
                    LoadToProjectKeepParamsCommand.NotifyCanExecuteChanged();
                }
            else
            {
                SelectedItem = null;
                IsSelectedFamilyStale = false;
                CanPlaceType = false;
            }
        }
        else
        {
            SelectedItem = null;
            IsSelectedFamilyStale = false;
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
            if (node is FamilyLeafNodeViewModel leaf && batch.TryGetValue(leaf.CatalogItemId, out var types))
            {
                foreach (var t in types)
                {
                    leaf.Children.Add(new FamilyTypeNodeViewModel(t.CatalogItemId, t.Name));
                }

                if (leaf.Children.Count == 0)
                {
                    leaf.Children.Add(new FamilyTypeNodeViewModel(leaf.CatalogItemId, leaf.DisplayName, isVirtual: true));
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
    /// Fire-and-forget helper for post-operations inside ExternalEvent callbacks.
    /// MUST be async void (not async Task) — ExternalEvent handler runs on Revit UI thread
    /// and must not capture or await any Task. See revit-api-best-practice/async-threading-patterns.md
    /// </summary>
    private static async void FireAndForget(Func<Task> taskFactory)
    {
        try
        {
            await taskFactory();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"FireAndForget: {ex}");
        }
    }

    /// <summary>
    /// Triggers tree refresh via ExternalEvent so that Revit API (FilteredElementCollector)
    /// runs in the correct thread context before LoadTreeAsync builds the UI.
    /// </summary>
    private void RefreshTreeViaExternalEvent()
    {
        _externalEvent.Raise(() =>
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

            var dispatcher = System.Windows.Application.Current?.Dispatcher
                ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

            dispatcher?.BeginInvoke(new Action(async () =>
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
                    SmartConLogger.Error($"RefreshTreeAsync failed: {ex}");
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_ErrorFormat) ?? "Error: {0}",
                        ex.Message);
                }
            }));
        });
    }

    [RelayCommand]
    private void RefreshTree()
    {
        RefreshTreeViaExternalEvent();
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

            var dispatcher = System.Windows.Application.Current?.Dispatcher
                ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

            if (dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    FireAndForget(async () => await LoadTreeAsync());
                }));
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"OnPlacementCompleted dispatcher invoke failed: {ex.Message}");
        }
    }

    private void OnPlacementFailed(string errorMessage)
    {
        StatusMessage = errorMessage;
        SmartConLogger.Warn($"[PlacementFailed] {errorMessage}");
    }

    private void OnPlacementSucceeded(string successMessage)
    {
        StatusMessage = successMessage;
        SmartConLogger.Info($"[PlacementSucceeded] {successMessage}");
    }

    private void OnPlacementStatusMessage(string statusMessage)
    {
        StatusMessage = statusMessage;
        SmartConLogger.Info($"[PlacementStatus] {statusMessage}");
    }

    public void Dispose()
    {
        _databaseManager.ActiveDatabaseChanged -= OnActiveDatabaseChanged;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        _placementDragService.PlacementCompleted -= OnPlacementCompleted;
        _placementDragService.PlacementFailed -= OnPlacementFailed;
        _placementDragService.PlacementSucceeded -= OnPlacementSucceeded;
        _placementDragService.PlacementStatusMessage -= OnPlacementStatusMessage;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }
}

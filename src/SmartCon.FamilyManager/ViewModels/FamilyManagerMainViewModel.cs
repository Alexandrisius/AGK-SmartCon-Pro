using System.Collections.ObjectModel;
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
public sealed partial class FamilyManagerMainViewModel : ObservableObject
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
    private CancellationTokenSource? _searchCts;
    private bool _suppressConnectionChanged;
    private CategoryNodeViewModel? _noCategoryNode;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private FamilyCatalogItemRow? _selectedItem;
    [ObservableProperty] private ObservableCollection<CatalogTreeNodeViewModel> _treeNodes = [];
    [ObservableProperty] private CatalogTreeNodeViewModel? _selectedTreeNode;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _totalItemCount;
    [ObservableProperty] private bool _canLoadToProject;
    [ObservableProperty] private ObservableCollection<DatabaseConnection> _connections = new();
    [ObservableProperty] private DatabaseConnection? _selectedConnection;
    [ObservableProperty] private int _currentRevitVersion;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExtractTypesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportDataCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFileToCategoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFolderToCategoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportDataForCategoryCommand))]
    private bool _canImport;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCategoryEditorCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditMetadataCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateFamilyCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteFamilyCommand))]
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
        IDbAccessControlService accessControl)
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

        _databaseManager.ActiveDatabaseChanged += OnActiveDatabaseChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;

        DetectRevitVersion();
        _ = InitializeAsync();
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

    private async Task InitializeAsync()
    {
        SmartConLogger.TruncateMainLog();
        SmartConLogger.Info($"======================================================================");
        SmartConLogger.Info($"FamilyManager SESSION START  Revit {CurrentRevitVersion}  [{DateTime.Now:yyyy-MM-dd HH:mm:ss}]");
        SmartConLogger.Info($"======================================================================");

        RefreshConnections();
        await RefreshAccessAndLoadTreeAsync();
    }

    private async Task RefreshAccessAndLoadTreeAsync()
    {
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

    private void UpdateAccessProperties()
    {
        CanImport = _accessControl.CanImport;
        CanEdit = _accessControl.CanEdit;
        CanManageUsers = _accessControl.CanManageUsers;
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        _ = LoadTreeAsync(_searchCts.Token);
    }

    partial void OnSelectedItemChanged(FamilyCatalogItemRow? value)
    {
        CanLoadToProject = value is not null && value.ContentStatus == ContentStatus.Active && _accessControl.CanLoadToProject;
        LoadToProjectCommand.NotifyCanExecuteChanged();
        LoadAndPlaceCommand.NotifyCanExecuteChanged();
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

        LoadToProjectCommand.NotifyCanExecuteChanged();
        LoadAndPlaceCommand.NotifyCanExecuteChanged();
        ImportFileToCategoryCommand.NotifyCanExecuteChanged();
        ImportFolderToCategoryCommand.NotifyCanExecuteChanged();
        ImportDataForCategoryCommand.NotifyCanExecuteChanged();
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
                    if (!string.IsNullOrWhiteSpace(t.Name))
                        leaf.Children.Add(new FamilyTypeNodeViewModel(t.CatalogItemId, t.Name));
                }

                if (expandedFamilyIds.Contains(leaf.CatalogItemId))
                    leaf.IsExpanded = true;
            }
            AttachTypesToNodes(node.Children, batch, expandedFamilyIds);
        }
    }

    [RelayCommand]
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
}

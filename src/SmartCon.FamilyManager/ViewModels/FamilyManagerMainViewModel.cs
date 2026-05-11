using System.Collections.ObjectModel;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Events;
using SmartCon.FamilyManager.Services;
using SmartCon.UI;
using SmartCon.UI.Behaviors;

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
    private readonly IAttributeDefinitionRepository _attributeDefRepository;
    private readonly ICategoryAttributeBindingService _bindingService;
    private readonly IFamilyMetadataPackageService _packageService;
    private CancellationTokenSource? _searchCts;
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
        IAttributeDefinitionRepository attributeDefRepository,
        ICategoryAttributeBindingService bindingService,
        IFamilyMetadataPackageService packageService)
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
        _attributeDefRepository = attributeDefRepository;
        _bindingService = bindingService;
        _packageService = packageService;

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
        await LoadTreeAsync();
    }

    private bool _suppressConnectionChanged;

    private void RefreshConnections()
    {
        _suppressConnectionChanged = true;
        try
        {
            var list = _databaseManager.ListConnections();
            Connections = new ObservableCollection<DatabaseConnection>(list);
            var active = _databaseManager.GetActiveConnection();
            SelectedConnection = Connections.FirstOrDefault(c => c.Id == active?.Id);
        }
        finally
        {
            _suppressConnectionChanged = false;
        }
    }

    private void OnActiveDatabaseChanged(object? sender, string connectionId)
    {
        RefreshConnections();
        _ = LoadTreeAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        _ = LoadTreeAsync(_searchCts.Token);
    }

    partial void OnSelectedItemChanged(FamilyCatalogItemRow? value)
    {
        CanLoadToProject = value is not null && value.ContentStatus == ContentStatus.Active;
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

    partial void OnSelectedConnectionChanged(DatabaseConnection? value)
    {
        if (value is null) return;
        if (_suppressConnectionChanged) return;
        var active = _databaseManager.GetActiveConnection();
        if (active?.Id == value.Id) return;
        _ = SwitchDatabaseAsync(value.Id);
    }

    private async Task SwitchDatabaseAsync(string connectionId)
    {
        IsLoading = true;
        try
        {
            var success = await _databaseManager.SwitchDatabaseAsync(connectionId);
            if (success)
            {
                var conn = Connections.FirstOrDefault(c => c.Id == connectionId);
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitched) ?? "Switched to: {0}",
                    conn?.Name ?? connectionId);
            }
            else
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitchError) ?? "Error switching database";
                RefreshConnections();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"{LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitchError) ?? "Error switching database"}: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadTreeAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            IReadOnlyList<Core.Models.FamilyManager.CategoryNode> categories = [];
            try
            {
                categories = await _categoryRepository.GetAllAsync(ct);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"LoadTreeAsync GetAllAsync failed: {ex.Message}");
            }

            var tree = new CategoryTree(categories);

            var query = new FamilyCatalogQuery(
                SearchText: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                CategoryFilter: null,
                StatusFilter: null,
                Tags: null,
                ManufacturerFilter: null,
                Sort: FamilyCatalogSort.NameAsc,
                Offset: 0,
                Limit: 500);

            var results = await _catalogProvider.SearchAsync(query, ct);
            TotalItemCount = await _catalogProvider.GetItemCountAsync(ct);

            var rootNodes = new ObservableCollection<CatalogTreeNodeViewModel>();
            var expandAll = !string.IsNullOrWhiteSpace(SearchText);

            var expandedIds = new HashSet<string>();
            var expandedFamilyIds = new HashSet<string>();
            if (!expandAll) CollectExpandedIds(TreeNodes, expandedIds, expandedFamilyIds);

            var itemsByCategory = results
                .GroupBy(i => i.CategoryId ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var catNode in tree.GetRootNodes())
            {
                var catVm = BuildCategoryNode(tree, catNode, itemsByCategory, expandAll, expandedIds);
                if (catVm is CategoryNodeViewModel { FamilyCount: > 0 }) rootNodes.Add(catVm);
            }

            var uncategorized = results.Where(r => string.IsNullOrEmpty(r.CategoryId)).ToList();
            var noCatLabel = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "No category";
            _noCategoryNode = new CategoryNodeViewModel(
                categoryId: "__no_category__",
                name: noCatLabel,
                parentId: null,
                fullPath: noCatLabel);
            foreach (var item in uncategorized)
            {
                _noCategoryNode.Children.Add(new FamilyLeafNodeViewModel(new FamilyCatalogItemRow
                {
                    Id = item.Id,
                    Name = item.Name,
                    CategoryId = item.CategoryId,
                    CategoryName = null,
                    Manufacturer = item.Manufacturer,
                    ContentStatus = item.ContentStatus,
                    CurrentVersionLabel = item.CurrentVersionLabel,
                    VersionLabel = item.CurrentVersionLabel,
                    UpdatedAtUtc = item.UpdatedAtUtc,
                    Tags = item.Tags,
                    Description = item.Description,
                }));
            }
            _noCategoryNode.FamilyCount = uncategorized.Count;
            if (!expandAll && expandedIds.Contains("__no_category__")) _noCategoryNode.IsExpanded = true;
            rootNodes.Add(_noCategoryNode);

            try
            {
                await AttachCachedTypesAsync(rootNodes, expandedFamilyIds, ct);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"AttachCachedTypesAsync failed: {ex.Message}");
            }

            TreeNodes = rootNodes;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private CatalogTreeNodeViewModel? BuildCategoryNode(CategoryTree tree, CategoryNode catNode, IReadOnlyDictionary<string, List<FamilyCatalogItem>> itemsByCategory, bool expandAll, HashSet<string>? expandedIds = null)
    {
        var vm = new CategoryNodeViewModel(catNode);
        var familyCount = 0;

        foreach (var child in tree.GetChildren(catNode.Id))
        {
            var childVm = BuildCategoryNode(tree, child, itemsByCategory, expandAll, expandedIds);
            if (childVm is CategoryNodeViewModel childCat)
            {
                if (childCat.FamilyCount > 0) vm.Children.Add(childVm);
                familyCount += childCat.FamilyCount;
            }
        }

        if (itemsByCategory.TryGetValue(catNode.Id, out var items))
        {
            familyCount += items.Count;
            foreach (var item in items)
            {
                vm.Children.Add(new FamilyLeafNodeViewModel(new FamilyCatalogItemRow
                {
                    Id = item.Id,
                    Name = item.Name,
                    CategoryId = item.CategoryId,
                    CategoryName = catNode.FullPath,
                    Manufacturer = item.Manufacturer,
                    ContentStatus = item.ContentStatus,
                    CurrentVersionLabel = item.CurrentVersionLabel,
                    VersionLabel = item.CurrentVersionLabel,
                    UpdatedAtUtc = item.UpdatedAtUtc,
                    Tags = item.Tags,
                    Description = item.Description,
                }));
            }
        }

        vm.FamilyCount = familyCount;
        if (familyCount > 0)
        {
            if (expandAll) vm.IsExpanded = true;
            else if (expandedIds is not null && expandedIds.Contains(catNode.Id)) vm.IsExpanded = true;
        }
        return vm;
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

    [RelayCommand]
    private async Task OpenCategoryEditorAsync()
    {
        var editorVm = _viewModelFactory.CreateCategoryTreeEditorViewModel();
        editorVm.Saved += () => _ = LoadTreeAsync();
        await editorVm.InitializeAsync();
        _dialogService.ShowCategoryTreeEditor(editorVm);
    }

    [RelayCommand]
    private async Task ImportFilesAsync()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_ImportFile) ?? "Import Files";
        var paths = _dialogService.ShowImportFilesDialog(title);
        if (paths is null || paths.Length == 0) return;

        IsLoading = true;
        try
        {
            var successCount = 0;
            var skipCount = 0;
            var errorCount = 0;

            for (var i = 0; i < paths.Length; i++)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                    i + 1, paths.Length);

                var request = new FamilyImportRequest(paths[i], CurrentRevitVersion, null, null, null);
                var result = await _importService.ImportFileAsync(request);

                if (result.WasSkippedAsDuplicate) skipCount++;
                else if (result.Success) successCount++;
                else errorCount++;
            }

            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportSuccess) ?? "Imported: {0}",
                $"{successCount} / {paths.Length}");

            await LoadTreeAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_ImportFolder) ?? "Import Folder";
        var path = _dialogService.ShowFolderBrowserDialog(title);
        if (string.IsNullOrWhiteSpace(path)) return;

        await ImportFolder(path!);
    }

    private async Task ImportFile(string path)
    {
        IsLoading = true;
        try
        {
            var request = new FamilyImportRequest(path, CurrentRevitVersion, null, null, null);
            var result = await _importService.ImportFileAsync(request);

            if (result.WasSkippedAsDuplicate)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_DuplicateSkipped) ?? "Duplicate skipped: {0}",
                    result.FileName);
            }
            else if (result.Success)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportSuccess) ?? "Imported: {0}",
                    result.FileName);
            }
            else
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}",
                    result.ErrorMessage);
            }

            await LoadTreeAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ImportFolder(string path)
    {
        IsLoading = true;
        try
        {
            var request = new FamilyFolderImportRequest(path, CurrentRevitVersion, true, null, null, null);
            var progress = new Progress<FamilyImportProgress>(p =>
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                    p.CurrentFileIndex + 1,
                    p.TotalFiles);
            });

            var result = await _importService.ImportFolderAsync(request, progress);

            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportSuccess) ?? "Imported: {0}",
                $"{result.SuccessCount} / {result.TotalFiles}");

            await LoadTreeAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanImportToCategory() =>
        SelectedTreeNode is CategoryNodeViewModel cat && cat.CategoryId != "__no_category__";

    private static List<FamilyLeafNodeViewModel> CollectFamiliesRecursive(CategoryNodeViewModel categoryNode)
    {
        var result = new List<FamilyLeafNodeViewModel>();
        foreach (var child in categoryNode.Children)
        {
            if (child is FamilyLeafNodeViewModel leaf)
                result.Add(leaf);
            else if (child is CategoryNodeViewModel cat)
                result.AddRange(CollectFamiliesRecursive(cat));
        }
        return result;
    }

    [RelayCommand(CanExecute = nameof(CanImportToCategory))]
    private async Task ImportFileToCategoryAsync()
    {
        if (SelectedTreeNode is not CategoryNodeViewModel categoryNode) return;
        if (categoryNode.CategoryId == "__no_category__") return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_ImportFile) ?? "Import File";
        var paths = _dialogService.ShowImportFilesDialog(title);
        if (paths is null || paths.Length == 0) return;

        IsLoading = true;
        try
        {
            var successCount = 0;
            var skipCount = 0;
            var errorCount = 0;

            for (var i = 0; i < paths.Length; i++)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                    i + 1, paths.Length);

                var request = new FamilyImportRequest(
                    paths[i], CurrentRevitVersion, null, null, null, categoryNode.CategoryId);
                var result = await _importService.ImportFileAsync(request);

                if (result.WasSkippedAsDuplicate) skipCount++;
                else if (result.Success) successCount++;
                else errorCount++;
            }

            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportSuccess) ?? "Imported: {0}",
                $"{successCount} / {paths.Length}");

            await LoadTreeAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportToCategory))]
    private async Task ImportFolderToCategoryAsync()
    {
        if (SelectedTreeNode is not CategoryNodeViewModel categoryNode) return;
        if (categoryNode.CategoryId == "__no_category__") return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_ImportFolder) ?? "Import Folder";
        var path = _dialogService.ShowFolderBrowserDialog(title);
        if (string.IsNullOrWhiteSpace(path)) return;

        IsLoading = true;
        try
        {
            var request = new FamilyFolderImportRequest(
                path!, CurrentRevitVersion, true, null, null, null, categoryNode.CategoryId);
            var progress = new Progress<FamilyImportProgress>(p =>
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                    p.CurrentFileIndex + 1,
                    p.TotalFiles);
            });

            var result = await _importService.ImportFolderAsync(request, progress);

            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportSuccess) ?? "Imported: {0}",
                $"{result.SuccessCount} / {result.TotalFiles}");

            await LoadTreeAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportToCategory))]
    private void ImportDataForCategory()
    {
        if (SelectedTreeNode is not CategoryNodeViewModel categoryNode) return;
        if (categoryNode.CategoryId == "__no_category__") return;

        var families = CollectFamiliesRecursive(categoryNode);
        if (families.Count == 0)
        {
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_NoFamiliesInCategory) ?? "No families in category";
            return;
        }

        IsLoading = true;
        StatusMessage = string.Empty;

        FireAndForget(async () =>
        {
            var preparedItems = new List<(string CatalogItemId, string Name, string? FilePath, IReadOnlyList<string> ParamNames, string? VersionId)>();
            var targetRevit = CurrentRevitVersion;

            for (var i = 0; i < families.Count; i++)
            {
                var family = families[i];
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportDataProgress) ?? "Preparing {0} of {1}: {2}",
                    i + 1, families.Count, family.DisplayName);

                var prepareResult = await _dataImportService.PrepareExtractionAsync(family.CatalogItemId, targetRevit, CancellationToken.None);
                if (!prepareResult.Success || string.IsNullOrEmpty(prepareResult.ResolvedFilePath))
                    continue;

                preparedItems.Add((
                    family.CatalogItemId,
                    family.DisplayName,
                    prepareResult.ResolvedFilePath,
                    prepareResult.ParameterNames,
                    prepareResult.Item?.CurrentVersionLabel));
            }

            if (preparedItems.Count == 0)
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_ImportPrepareError) ?? "Preparation error";
                IsLoading = false;
                return;
            }

            _externalEvent.Raise(() =>
            {
                try
                {
                    SmartConLogger.FreezeThreadPool("ImportDataForCategory.ExternalEvent.Start");
                    var successCount = 0;
                    var errorCount = 0;

                    for (var i = 0; i < preparedItems.Count; i++)
                    {
                        var item = preparedItems[i];
                        try
                        {
                            StatusMessage = string.Format(
                                LanguageManager.GetString(StringLocalization.Keys.FM_ImportDataProgress) ?? "Processing {0} of {1}: {2}",
                                i + 1, preparedItems.Count, item.Name);

                            var extractionResult = SmartConLogger.FreezeTimer($"ImportDataForCategory.Extract[{item.Name}]", () =>
                                _extractionService.Extract(item.FilePath!, item.ParamNames));

                            SmartConLogger.FreezeTimer($"ImportDataForCategory.SaveResult[{item.Name}]", () =>
                                _dataImportService.SaveExtractionResultAsync(
                                    item.CatalogItemId, extractionResult, item.VersionId, null, CancellationToken.None)
                                    .ConfigureAwait(false).GetAwaiter().GetResult());

                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            errorCount++;
                            SmartConLogger.Freeze($"ImportDataForCategory: Failed for {item.Name} - {ex.Message}");
                            SmartConLogger.Warn($"ImportData failed for {item.Name}: {ex.Message}");
                        }
                    }

                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_ImportResultFormat) ?? "Imported: {0} types, {1} values found",
                        $"{successCount}/{preparedItems.Count} families", "see log");

                    SmartConLogger.Freeze($"ImportDataForCategory: Completed {successCount}/{preparedItems.Count}");

                    FireAndForget(async () => await LoadTreeAsync());
                }
                catch (Exception ex)
                {
                    SmartConLogger.Freeze($"ImportDataForCategory: Exception - {ex.GetType().Name}: {ex.Message}");
                    SmartConLogger.Warn($"ImportDataForCategory failed: {ex.Message}");
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}",
                        ex.Message);
                }
                finally
                {
                    IsLoading = false;
                }
            });
        });
    }

    [RelayCommand(CanExecute = nameof(CanLoadToProject))]
    private void LoadToProject()
    {
        if (SelectedItem is null) return;

        var selectedId = SelectedItem.Id;
        var selectedName = SelectedItem.Name;
        var targetRevit = CurrentRevitVersion;

        _externalEvent.Raise(() =>
        {
            var doc = _revitContext.GetDocument();
            if (doc is null)
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_NoActiveDocument) ?? "No active document";
                return;
            }

            try
            {
                var resolved = Task.Run(() => _fileResolver.ResolveForLoadAsync(selectedId, targetRevit, CancellationToken.None)).GetAwaiter().GetResult();

                if (string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_NoVersionSelected) ?? "No version available for this Revit version";
                    return;
                }

                var loadOptions = FamilyLoadOptions.Default with { PreferredName = selectedName };
                var result = _loadService.LoadFamilyAsync(resolved, loadOptions, CancellationToken.None).GetAwaiter().GetResult();

                if (result.Success)
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadSuccess) ?? "Family \"{0}\" loaded",
                        result.FamilyName ?? selectedName);

                    var familyName = result.FamilyName ?? selectedName;
                    var loadedFamily = FindLoadedFamily(doc, familyName);
                    if (loadedFamily is not null)
                    {
                        var typeNames = ReadFamilyTypeNames(doc, loadedFamily);
                        if (typeNames.Count > 0)
                            FireAndForget(() => SaveTypesAndReloadTreeAsync(selectedId, typeNames));
                    }

                    var usage = new ProjectFamilyUsage(
                        Id: Guid.NewGuid().ToString(),
                        CatalogItemId: selectedId,
                        VersionId: resolved.VersionId,
                        ProjectName: Path.GetFileName(doc.PathName),
                        ProjectPath: doc.PathName,
                        RevitMajorVersion: targetRevit,
                        Action: "Load",
                        CreatedAtUtc: DateTimeOffset.UtcNow);

                    FireAndForget(() => _usageRepo.RecordUsageAsync(usage, CancellationToken.None));
                }
                else
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                        result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                    ex.Message);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanLoadToProject))]
    private void LoadAndPlace()
    {
        if (SelectedItem is null) return;

        var selectedId = SelectedItem.Id;
        var selectedName = SelectedItem.Name;
        var targetRevit = CurrentRevitVersion;

        _externalEvent.RaiseWithApplication(appObj =>
        {
            var uiApp = (UIApplication)appObj;
            var doc = _revitContext.GetDocument();
            if (doc is null)
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_NoActiveDocument) ?? "No active document";
                return;
            }

            try
            {
                SmartConLogger.FreezeThreadPool("LoadAndPlace.Start");

                var resolved = SmartConLogger.FreezeTimer("LoadAndPlace.ResolveFile", () =>
                    Task.Run(() => _fileResolver.ResolveForLoadAsync(selectedId, targetRevit, CancellationToken.None)).GetAwaiter().GetResult());

                if (string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_NoVersionSelected) ?? "No version available";
                    SmartConLogger.Freeze("LoadAndPlace: No version available");
                    return;
                }

                var loadOptions = FamilyLoadOptions.Default with { PreferredName = selectedName };
                var result = SmartConLogger.FreezeTimer("LoadAndPlace.LoadFamily", () =>
                    _loadService.LoadFamilyAsync(resolved, loadOptions, CancellationToken.None).GetAwaiter().GetResult());

                if (!result.Success)
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                        result.ErrorMessage);
                    SmartConLogger.Freeze($"LoadAndPlace: Load failed - {result.ErrorMessage}");
                    return;
                }

                SmartConLogger.Freeze($"LoadAndPlace: Family '{result.FamilyName}' loaded successfully");

                var familyName = result.FamilyName ?? selectedName;

                var family = FindLoadedFamily(doc, familyName);

                if (family is null)
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                        LanguageManager.GetString(StringLocalization.Keys.FM_FamilyNotFoundAfterLoad) ?? "Family not found after loading");
                    return;
                }

                var symbolId = family.GetFamilySymbolIds().Cast<ElementId>().FirstOrDefault();
                if (symbolId is null) return;

                var symbol = doc.GetElement(symbolId) as FamilySymbol;
                if (symbol is null) return;

                if (!symbol.IsActive)
                {
                    _transactionService.RunInTransaction("Activate Family Symbol", _ =>
                    {
                        if (!symbol.IsActive)
                            symbol.Activate();
                    });
                }

                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc is not null)
                {
                    uidoc.PostRequestForElementTypePlacement(symbol);
                }

                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_LoadAndPlaceSuccess) ?? "Family \"{0}\" — click to place",
                    familyName);

                var usage = new ProjectFamilyUsage(
                    Id: Guid.NewGuid().ToString(),
                    CatalogItemId: selectedId,
                    VersionId: resolved.VersionId,
                    ProjectName: Path.GetFileName(doc.PathName),
                    ProjectPath: doc.PathName,
                    RevitMajorVersion: targetRevit,
                    Action: "LoadAndPlace",
                    CreatedAtUtc: DateTimeOffset.UtcNow);

                FireAndForget(() => _usageRepo.RecordUsageAsync(usage, CancellationToken.None));
                SmartConLogger.Freeze("LoadAndPlace: Completed successfully");
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                    ex.Message);
                SmartConLogger.Freeze($"LoadAndPlace: Exception - {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    [RelayCommand]
    private async Task EditMetadata()
    {
        if (SelectedItem is null) return;

        var itemId = SelectedItem.Id;
        var vm = _viewModelFactory.CreateMetadataEditViewModel(
            SelectedItem.Id,
            SelectedItem.Name,
            SelectedItem.Description,
            SelectedItem.CategoryId,
            SelectedItem.CategoryName,
            SelectedItem.Tags,
            SelectedItem.ContentStatus);

        var result = _dialogService.ShowMetadataEdit(vm);
        if (result != true) return;

        await LoadTreeAsync();
        ExpandAndSelectItem(itemId);
    }

    [RelayCommand]
    private async Task OpenProperties()
    {
        if (SelectedItem is null) return;

        var itemId = SelectedItem.Id;
        var updatedAt = SelectedItem.UpdatedAtUtc != default
            ? SelectedItem.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : null;

        var vm = _viewModelFactory.CreatePropertiesViewModel(
            SelectedItem.Id,
            SelectedItem.Name,
            SelectedItem.Description,
            SelectedItem.CategoryId,
            SelectedItem.CategoryName,
            SelectedItem.Tags,
            SelectedItem.ContentStatus,
            SelectedItem.Manufacturer,
            SelectedItem.VersionLabel,
            null,
            null,
            updatedAt);

        vm.InitializeCommand.Execute(null);
        var result = _dialogService.ShowProperties(vm);
        if (result != true) return;

        await LoadTreeAsync();
        ExpandAndSelectItem(itemId);
    }

    [RelayCommand]
    private async Task CreateDatabaseAsync()
    {
        var path = _dialogService.ShowFolderBrowserDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_DbSelectPath) ?? "Select parent folder for database");
        if (string.IsNullOrWhiteSpace(path)) return;

        var name = _dialogService.ShowInputDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_DbNewTitle) ?? "New Database",
            LanguageManager.GetString(StringLocalization.Keys.FM_DbNewPrompt) ?? "Enter database name:",
            LanguageManager.GetString(StringLocalization.Keys.FM_DbNewDefault) ?? "New Catalog");

        if (string.IsNullOrWhiteSpace(name)) return;

        IsLoading = true;
        try
        {
            var conn = await _databaseManager.CreateDatabaseAsync(name!.Trim(), path!);
            RefreshConnections();
            SelectedConnection = Connections.FirstOrDefault(c => c.Id == conn.Id);
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbCreated) ?? "Database \"{0}\" created at {1}",
                conn.Name, conn.Path);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbCreateError) ?? "Error creating database: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ConnectDatabaseAsync()
    {
        var path = _dialogService.ShowFolderBrowserDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_DbSelectPath) ?? "Select database folder");
        if (string.IsNullOrWhiteSpace(path)) return;

        IsLoading = true;
        try
        {
            var conn = await _databaseManager.ConnectDatabaseAsync(path!);
            RefreshConnections();
            SelectedConnection = Connections.FirstOrDefault(c => c.Id == conn.Id);
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitched) ?? "Connected to: {0}",
                conn.Name);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbCreateError) ?? "Error connecting: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectDatabaseAsync()
    {
        if (SelectedConnection is null) return;

        var connections = _databaseManager.ListConnections();
        if (connections.Count <= 1)
        {
            _dialogService.ShowWarning(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleteTitle) ?? "Disconnect",
                LanguageManager.GetString(StringLocalization.Keys.FM_CannotDisconnectOnlyDatabase) ?? "Cannot disconnect the only database.");
            return;
        }

        IsLoading = true;
        try
        {
            var success = await _databaseManager.DisconnectDatabaseAsync(SelectedConnection.Id);
            if (success)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleted) ?? "Disconnected: {0}",
                    SelectedConnection.Name);
                RefreshConnections();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ErrorFormat) ?? "Error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DeleteDatabaseAsync()
    {
        if (SelectedConnection is null) return;

        var isActive = _databaseManager.GetActiveConnection()?.Id == SelectedConnection.Id;
        var connections = _databaseManager.ListConnections();
        if (isActive && connections.Count <= 1)
        {
            _dialogService.ShowWarning(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleteTitle) ?? "Delete Database",
                LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleteSingle) ?? "Cannot delete the only database.");
            return;
        }

        var confirm = _dialogService.ShowInputDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleteTitle) ?? "Delete Database",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbDeletePrompt) ?? "Enter \"{0}\" to confirm deletion:",
                SelectedConnection.Name),
            "");

        if (confirm != SelectedConnection.Name) return;

        IsLoading = true;
        try
        {
            var success = await _databaseManager.DeleteDatabaseAsync(SelectedConnection.Id);
            if (success)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleted) ?? "Database \"{0}\" deleted",
                    SelectedConnection.Name);
                RefreshConnections();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleteError) ?? "Error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task UpdateFamilyAsync()
    {
        if (SelectedTreeNode is not FamilyLeafNodeViewModel leaf) return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_Update) ?? "Update";
        var path = _dialogService.ShowOpenFileDialog(title);
        if (path is null) return;

        IsLoading = true;
        try
        {
            var request = new FamilyUpdateRequest(leaf.CatalogItemId, path, CurrentRevitVersion);
            var result = await _importService.UpdateFamilyAsync(request);

            if (result.WasSkippedAsDuplicate)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_UpdateIdentical) ?? "File is identical to current version: {0}",
                    result.FileName);
            }
            else if (result.Success)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_UpdateSuccess) ?? "Updated: {0} → {1}",
                    result.FileName,
                    result.VersionLabel);
            }
            else
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_UpdateError) ?? "Update error: {0}",
                    result.ErrorMessage);
            }

            await LoadTreeAsync();
            ExpandAndSelectItem(leaf.CatalogItemId);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_UpdateError) ?? "Update error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DeleteFamilyAsync()
    {
        if (SelectedItem is null) return;

        var confirmed = _dialogService.ShowConfirmation(
            LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeleteTitle) ?? "Delete Family",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeletePrompt) ?? "Delete \"{0}\"?",
                SelectedItem.Name));

        if (!confirmed) return;

        IsLoading = true;
        try
        {
            var success = await _writableProvider.DeleteItemAsync(SelectedItem.Id);
            if (success)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeleted) ?? "Deleted: {0}",
                    SelectedItem.Name);
                await LoadTreeAsync();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"{LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeleteError) ?? "Error"}: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task MoveFamilyToCategoryAsync(string familyId, string? targetCategoryId)
    {
        IsLoading = true;
        try
        {
            await _writableProvider.UpdateItemAsync(familyId, null, null, targetCategoryId, null, null);
            await LoadTreeAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartDrag))]
    private void StartDrag(object? item)
    {
        // Drag permission gate. Behavior handles the actual DoDragDrop.
    }

    private bool CanStartDrag(object? item) => item is FamilyLeafNodeViewModel;

    [RelayCommand(CanExecute = nameof(CanDropFamily))]
    private async Task DropFamilyAsync(TreeViewDropInfo? info)
    {
        if (info is not { Payload: FamilyLeafNodeViewModel leaf, Target: CategoryNodeViewModel target })
            return;

        var categoryId = target.CategoryId == "__no_category__"
            ? null
            : target.CategoryId;

        await MoveFamilyToCategoryAsync(leaf.CatalogItemId, categoryId);
    }

    private bool CanDropFamily(TreeViewDropInfo? info)
    {
        if (info is null) return false;
        return info.Payload is FamilyLeafNodeViewModel
            && info.Target is CategoryNodeViewModel;
    }

    [RelayCommand(CanExecute = nameof(CanLoadToProject))]
    private void ExtractTypes()
    {
        if (SelectedItem is null) return;

        var selectedId = SelectedItem.Id;
        var selectedName = SelectedItem.Name;
        var targetRevit = CurrentRevitVersion;

        _externalEvent.Raise(() =>
        {
            var doc = _revitContext.GetDocument();
            if (doc is null) return;

            try
            {
                SmartConLogger.FreezeThreadPool("ExtractTypes.Start");

                var family = FindLoadedFamily(doc, selectedName);

                if (family is not null)
                {
                    var names = ReadFamilyTypeNames(doc, family);
                    SmartConLogger.Freeze($"ExtractTypes: Family already loaded, found {names.Count} types");
                    if (names.Count > 0)
                        FireAndForget(() => SaveTypesAndReloadTreeAsync(selectedId, names));
                    return;
                }

                var resolved = SmartConLogger.FreezeTimer("ExtractTypes.ResolveFile", () =>
                    _fileResolver.ResolveForLoadAsync(selectedId, targetRevit, CancellationToken.None).GetAwaiter().GetResult());

                if (string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    SmartConLogger.Freeze("ExtractTypes: No file resolved");
                    return;
                }

                List<string>? typeNames = null;
                SmartConLogger.FreezeTimer("ExtractTypes.LoadFamily", () =>
                {
                    _transactionService.RunAndRollback("Extract Types", d =>
                    {
                        if (!d.LoadFamily(resolved.AbsolutePath, new SimpleFamilyLoadOptions(), out var loaded) || loaded is null) return;
                        typeNames = ReadFamilyTypeNames(d, loaded);
                    });
                });

                SmartConLogger.Freeze($"ExtractTypes: Extracted {typeNames?.Count ?? 0} types");

                if (typeNames is not null && typeNames.Count > 0)
                    FireAndForget(() => SaveTypesAndReloadTreeAsync(selectedId, typeNames));
            }
            catch (Exception ex)
            {
                SmartConLogger.Freeze($"ExtractTypes: Exception - {ex.GetType().Name}: {ex.Message}");
                SmartConLogger.Warn($"ExtractTypes failed: {ex.Message}");
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanLoadToProject))]
    private void ImportData()
    {
        if (SelectedItem is null) return;

        var selectedId = SelectedItem.Id;
        var targetRevit = CurrentRevitVersion;

        IsLoading = true;
        StatusMessage = string.Empty;

        FireAndForget(async () =>
        {
            var prepareResult = await _dataImportService.PrepareExtractionAsync(selectedId, targetRevit, CancellationToken.None);

            if (!prepareResult.Success)
            {
                StatusMessage = prepareResult.ErrorMessage ?? LanguageManager.GetString(StringLocalization.Keys.FM_ImportPrepareError) ?? "Preparation error";
                IsLoading = false;
                return;
            }

            if (string.IsNullOrEmpty(prepareResult.ResolvedFilePath))
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_FamilyFileNotFound) ?? "Family file not found";
                IsLoading = false;
                return;
            }

            var rfaPath = prepareResult.ResolvedFilePath;
            var paramNames = prepareResult.ParameterNames;
            var versionId = prepareResult.Item?.CurrentVersionLabel;

            _externalEvent.Raise(() =>
            {
                try
                {
                    SmartConLogger.FreezeThreadPool("ImportData.ExternalEvent.Start");

                    var extractionResult = SmartConLogger.FreezeTimer("ImportData.Extract", () =>
                        _extractionService.Extract(rfaPath!, paramNames));

                    var saveResult = SmartConLogger.FreezeTimer("ImportData.SaveResult", () =>
                        _dataImportService.SaveExtractionResultAsync(
                            selectedId, extractionResult, versionId, null, CancellationToken.None).GetAwaiter().GetResult());

                    StatusMessage = saveResult.Success
                        ? string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportResultFormat) ?? "Imported: {0} types, {1} values found", saveResult.TypesCount, saveResult.AttributesFoundCount)
                        : string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportDataError) ?? "Import error: {0}", saveResult.ErrorMessage);

                    SmartConLogger.Freeze($"ImportData: Success={saveResult.Success}, Types={saveResult.TypesCount}");

                    FireAndForget(async () => await LoadTreeAsync());
                }
                catch (Exception ex)
                {
                    SmartConLogger.Freeze($"ImportData: Exception - {ex.GetType().Name}: {ex.Message}");
                    SmartConLogger.Warn($"ImportData failed: {ex.Message}");
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}",
                        ex.Message);
                }
                finally
                {
                    IsLoading = false;
                }
            });
        });
    }

    [RelayCommand]
    private void PlaceType()
    {
        if (SelectedTreeNode is not FamilyTypeNodeViewModel typeNode) return;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return;

        var catalogItemId = leaf.CatalogItemId;
        var familyName = leaf.DisplayName;
        var typeName = typeNode.TypeName;
        var targetRevit = CurrentRevitVersion;

        _externalEvent.RaiseWithApplication(appObj =>
        {
            var uiApp = (UIApplication)appObj;
            var doc = _revitContext.GetDocument();
            if (doc is null) return;

            try
            {
                SmartConLogger.FreezeThreadPool("PlaceType.Start");

                var family = FindLoadedFamily(doc, familyName);

                if (family is null)
                {
                    var resolved = SmartConLogger.FreezeTimer("PlaceType.ResolveFile", () =>
                        _fileResolver.ResolveForLoadAsync(catalogItemId, targetRevit, CancellationToken.None).GetAwaiter().GetResult());

                    if (string.IsNullOrEmpty(resolved.AbsolutePath))
                    {
                        SmartConLogger.Freeze("PlaceType: No file resolved");
                        return;
                    }

                    var loadOptions = FamilyLoadOptions.Default with { PreferredName = familyName };
                    SmartConLogger.FreezeTimer("PlaceType.LoadFamily", () =>
                        _loadService.LoadFamilyAsync(resolved, loadOptions, CancellationToken.None).GetAwaiter().GetResult());

                    family = FindLoadedFamily(doc, familyName);
                }
                else
                {
                    SmartConLogger.Freeze("PlaceType: Family already loaded");
                }

                if (family is null) return;

                var symbol = FindFamilySymbolByName(doc, family, typeName);
                if (symbol is null) return;

                if (!symbol.IsActive)
                {
                    _transactionService.RunInTransaction("Activate Symbol", _ =>
                    {
                        if (!symbol.IsActive) symbol.Activate();
                    });
                }

                uiApp.ActiveUIDocument?.PostRequestForElementTypePlacement(symbol);

                FireAndForget(async () =>
                {
                    await SaveTypesAndReloadTreeAsync(catalogItemId, ReadFamilyTypeNames(doc, family));
                });
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"PlaceType failed: {ex.Message}");
            }
        });
    }

    private async Task SaveTypesAndReloadTreeAsync(string catalogItemId, List<string> typeNames)
    {
        var types = typeNames.Select((name, i) => new FamilyTypeDescriptor(
            Guid.NewGuid().ToString(), catalogItemId, name, i)).ToList();

        await _typeRepository.SaveTypesAsync(catalogItemId, types.AsReadOnly(), CancellationToken.None);
        await LoadTreeAsync();
    }

    private static Autodesk.Revit.DB.Family? FindLoadedFamily(Document doc, string familyName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.Family))
            .Cast<Autodesk.Revit.DB.Family>()
            .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> ReadFamilyTypeNames(Document doc, Autodesk.Revit.DB.Family family)
    {
        return family.GetFamilySymbolIds()
            .Select(id => doc.GetElement(id))
            .OfType<FamilySymbol>()
            .Select(s => s.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static FamilySymbol? FindFamilySymbolByName(Document doc, Autodesk.Revit.DB.Family family, string typeName)
    {
        return family.GetFamilySymbolIds()
            .Select(id => doc.GetElement(id))
            .OfType<FamilySymbol>()
            .FirstOrDefault(s => s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task AttachCachedTypesAsync(ObservableCollection<CatalogTreeNodeViewModel> rootNodes, HashSet<string> expandedFamilyIds, CancellationToken ct)
    {
        var familyIds = new List<string>();
        CollectFamilyIds(rootNodes, familyIds);
        if (familyIds.Count == 0) return;

        var batch = await _typeRepository.GetAllTypesBatchAsync(familyIds, ct);

        AttachTypesToNodes(rootNodes, batch, expandedFamilyIds);
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
                    leaf.Children.Add(new FamilyTypeNodeViewModel(t.CatalogItemId, t.Name));

                if (expandedFamilyIds.Contains(leaf.CatalogItemId))
                    leaf.IsExpanded = true;
            }
            AttachTypesToNodes(node.Children, batch, expandedFamilyIds);
        }
    }

    private sealed class SimpleFamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(Autodesk.Revit.DB.Family sharedFamily, bool familyInUse,
            out Autodesk.Revit.DB.FamilySource source, out bool overwriteParameterValues)
        {
            source = Autodesk.Revit.DB.FamilySource.Family;
            overwriteParameterValues = true;
            return true;
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

    private void ExpandAndSelectItem(string catalogItemId)
    {
        foreach (var root in TreeNodes)
        {
            if (ExpandToItem(root, catalogItemId))
                return;
        }
    }

    private bool ExpandToItem(CatalogTreeNodeViewModel node, string catalogItemId)
    {
        foreach (var child in node.Children)
        {
            if (child is FamilyLeafNodeViewModel leaf && leaf.CatalogItemId == catalogItemId)
            {
                node.IsExpanded = true;
                leaf.IsSelected = true;
                SelectedTreeNode = leaf;
                return true;
            }
            if (ExpandToItem(child, catalogItemId))
            {
                node.IsExpanded = true;
                return true;
            }
        }
        return false;
    }
}

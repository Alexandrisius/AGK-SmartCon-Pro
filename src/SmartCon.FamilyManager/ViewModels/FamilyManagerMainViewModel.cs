using System.Collections.ObjectModel;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Events;
using SmartCon.FamilyManager.Services;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

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
    private CancellationTokenSource? _searchCts;

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
        ICategoryRepository categoryRepository)
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

        _databaseManager.ActiveDatabaseChanged += OnActiveDatabaseChanged;

        DetectRevitVersion();
        FireAndForget(InitializeAsync);
    }

    private void DetectRevitVersion()
    {
        try
        {
            var doc = _revitContext.GetDocument();
            if (doc?.Application?.VersionNumber is string versionStr && int.TryParse(versionStr, out var v))
            {
                CurrentRevitVersion = v;
            }
        }
        catch { }
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
        FireAndForget(() => LoadTreeAsync());
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        FireAndForget(() => LoadTreeAsync(_searchCts.Token));
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
                ContentStatus = leaf.ContentStatus,
                VersionLabel = leaf.VersionLabel,
                UpdatedAtUtc = leaf.UpdatedAtUtc,
                Tags = leaf.Tags,
                Description = leaf.Description,
            };
        }
        else
        {
            SelectedItem = null;
        }
    }

    partial void OnSelectedConnectionChanged(DatabaseConnection? value)
    {
        if (value is null) return;
        if (_suppressConnectionChanged) return;
        var active = _databaseManager.GetActiveConnection();
        if (active?.Id == value.Id) return;
        FireAndForget(() => SwitchDatabaseAsync(value.Id));
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
            catch
            {
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

            foreach (var catNode in tree.GetRootNodes())
            {
                var catVm = BuildCategoryNode(tree, catNode, results, expandAll);
                if (catVm is CategoryNodeViewModel { FamilyCount: > 0 }) rootNodes.Add(catVm);
            }

            var uncategorized = results.Where(r => string.IsNullOrEmpty(r.CategoryId)).ToList();
            var noCatLabel = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "No category";
            var noCatNode = new CategoryNodeViewModel(
                categoryId: "__no_category__",
                name: noCatLabel,
                parentId: null,
                fullPath: noCatLabel);
            foreach (var item in uncategorized)
            {
                noCatNode.Children.Add(new FamilyLeafNodeViewModel(new FamilyCatalogItemRow
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
            noCatNode.FamilyCount = uncategorized.Count;
            rootNodes.Add(noCatNode);

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

    private CatalogTreeNodeViewModel? BuildCategoryNode(CategoryTree tree, CategoryNode catNode, IReadOnlyList<FamilyCatalogItem> allItems, bool expandAll = false)
    {
        var vm = new CategoryNodeViewModel(catNode);

        foreach (var child in tree.GetChildren(catNode.Id))
        {
            var childVm = BuildCategoryNode(tree, child, allItems, expandAll);
            if (childVm is CategoryNodeViewModel { FamilyCount: > 0 }) vm.Children.Add(childVm);
        }

        foreach (var item in allItems.Where(i => i.CategoryId == catNode.Id))
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

        vm.FamilyCount = CountFamiliesRecursive(vm);
        if (expandAll && vm.FamilyCount > 0) vm.IsExpanded = true;
        return vm;
    }

    private static int CountFamiliesRecursive(CatalogTreeNodeViewModel node)
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

    [RelayCommand]
    private async Task OpenCategoryEditorAsync()
    {
        var editorVm = new CategoryTreeEditorViewModel(_categoryRepository, _dialogService);
        await editorVm.InitializeAsync();
        var result = _dialogService.ShowCategoryTreeEditor(editorVm);
        if (result == true)
        {
            await LoadTreeAsync();
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_Import) ?? "Import";
        var path = _dialogService.ShowImportDialog(title);
        if (path is null) return;

        if (Directory.Exists(path))
        {
            await ImportFolder(path);
        }
        else if (File.Exists(path))
        {
            await ImportFile(path);
        }
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
                var resolved = Task.Run(() => _fileResolver.ResolveForLoadAsync(selectedId, targetRevit, CancellationToken.None)).GetAwaiter().GetResult();

                if (string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_NoVersionSelected) ?? "No version available";
                    return;
                }

                var loadOptions = FamilyLoadOptions.Default with { PreferredName = selectedName };
                var result = _loadService.LoadFamilyAsync(resolved, loadOptions, CancellationToken.None).GetAwaiter().GetResult();

                if (!result.Success)
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                        result.ErrorMessage);
                    return;
                }

                var familyName = result.FamilyName ?? selectedName;

                var family = new FilteredElementCollector(doc)
                    .OfClass(typeof(Autodesk.Revit.DB.Family))
                    .Cast<Autodesk.Revit.DB.Family>()
                    .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));

                if (family is null)
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                        "Family not found after loading");
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
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                    ex.Message);
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
                "Cannot disconnect the only database.");
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
            StatusMessage = $"Error: {ex.Message}";
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

    private static async void FireAndForget(Func<Task> taskFactory)
    {
        try
        {
            await taskFactory();
        }
        catch
        {
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

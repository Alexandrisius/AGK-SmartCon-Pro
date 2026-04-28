using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private CancellationTokenSource? _searchCts;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private FamilyCatalogItemRow? _selectedItem;
    [ObservableProperty] private ICollectionView? _familyItemsView;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _totalItemCount;
    [ObservableProperty] private bool _canLoadToProject;
    [ObservableProperty] private ObservableCollection<DatabaseInfo> _databases = new();
    [ObservableProperty] private DatabaseInfo? _selectedDatabase;

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
        IDatabaseManager databaseManager)
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

        _databaseManager.ActiveDatabaseChanged += OnActiveDatabaseChanged;

        // Initialize empty view to prevent column collapse
        FamilyItemsView = new ListCollectionView(new ObservableCollection<FamilyCatalogItemRow>());

        // Initial load
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        RefreshDatabases();
        await SearchAsync();
    }

    private void RefreshDatabases()
    {
        var list = _databaseManager.ListDatabases();
        Databases = new ObservableCollection<DatabaseInfo>(list);
        SelectedDatabase = Databases.FirstOrDefault(d => d.Id == _databaseManager.GetActiveDatabaseId());
    }

    private void OnActiveDatabaseChanged(object? sender, string databaseId)
    {
        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            RefreshDatabases();
            _ = SearchAsync();
        });
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        _ = SearchAsync(_searchCts.Token);
    }

    partial void OnSelectedItemChanged(FamilyCatalogItemRow? value)
    {
        CanLoadToProject = value is not null;
    }

    partial void OnSelectedDatabaseChanged(DatabaseInfo? value)
    {
        if (value is null) return;
        if (value.Id == _databaseManager.GetActiveDatabaseId()) return;
        _ = SwitchDatabaseAsync(value.Id);
    }

    private async Task SwitchDatabaseAsync(string databaseId)
    {
        IsLoading = true;
        try
        {
            var success = await _databaseManager.SwitchDatabaseAsync(databaseId);
            if (success)
            {
                var dbName = Databases.FirstOrDefault(d => d.Id == databaseId)?.Name ?? databaseId;
                StatusMessage = $"База данных переключена на: {dbName}";
            }
            else
            {
                StatusMessage = "Ошибка переключения базы данных";
                RefreshDatabases();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Ошибка: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SearchAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var query = new FamilyCatalogQuery(
                SearchText: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                CategoryFilter: null,
                StatusFilter: null,
                Tags: null,
                ManufacturerFilter: null,
                Sort: FamilyCatalogSort.NameAsc,
                Offset: 0,
                Limit: 100);

            var results = await _catalogProvider.SearchAsync(query, ct);
            TotalItemCount = await _catalogProvider.GetItemCountAsync(ct);

            var rows = new ObservableCollection<FamilyCatalogItemRow>();
            foreach (var item in results)
            {
                string? versionLabel = null;
                if (item.CurrentVersionId is not null)
                {
                    var versions = await _catalogProvider.GetVersionsAsync(item.Id, ct);
                    var current = versions.FirstOrDefault(v => v.Id == item.CurrentVersionId);
                    versionLabel = current?.VersionLabel;
                }

                rows.Add(new FamilyCatalogItemRow
                {
                    Id = item.Id,
                    Name = item.Name,
                    CategoryName = string.IsNullOrWhiteSpace(item.CategoryName) ? "Без категории" : item.CategoryName,
                    Manufacturer = item.Manufacturer,
                    Status = item.Status,
                    CurrentVersionId = item.CurrentVersionId,
                    VersionLabel = versionLabel,
                    UpdatedAtUtc = item.UpdatedAtUtc,
                    Tags = item.Tags,
                    Description = item.Description
                });
            }

            var view = new ListCollectionView(rows);
            view.GroupDescriptions.Add(new PropertyGroupDescription("CategoryName"));
            view.SortDescriptions.Add(new SortDescription("CategoryName", ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
            FamilyItemsView = view;
        }
        catch (OperationCanceledException)
        {
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

    [RelayCommand]
    private async Task ImportFileAsync()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_ImportFile) ?? "Import File";
        var path = _dialogService.ShowOpenFileDialog(title);
        if (path is null) return;

        IsLoading = true;
        try
        {
            var request = new FamilyImportRequest(path, null, null, null);
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

            await SearchAsync();
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
        if (path is null) return;

        IsLoading = true;
        try
        {
            var request = new FamilyFolderImportRequest(path, true, null, null, null);
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

            await SearchAsync();
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
    private void LoadToProject()
    {
        if (SelectedItem is null) return;

        var selectedId = SelectedItem.Id;
        var selectedVersionId = SelectedItem.CurrentVersionId;
        var selectedName = SelectedItem.Name;

        _externalEvent.Raise(() =>
        {
            var doc = _revitContext.GetDocument();
            if (doc is null)
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_NoActiveDocument) ?? "No active document";
                return;
            }

            if (selectedVersionId is null)
            {
                StatusMessage = "No version selected";
                return;
            }

            try
            {
                var resolved = _fileResolver.ResolveForLoadAsync(selectedVersionId, CancellationToken.None).Result;
                var loadOptions = FamilyLoadOptions.Default with { PreferredName = selectedName };
                var result = _loadService.LoadFamilyAsync(resolved, loadOptions, CancellationToken.None).Result;

                if (result.Success)
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadSuccess) ?? "Family \"{0}\" loaded",
                        result.FamilyName ?? selectedName);

                    var usage = new ProjectFamilyUsage(
                        Id: Guid.NewGuid().ToString(),
                        CatalogItemId: selectedId,
                        VersionId: selectedVersionId,
                        ProviderId: string.Empty,
                        ProjectFingerprint: doc.PathName ?? string.Empty,
                        Action: "Load",
                        CreatedAtUtc: DateTimeOffset.UtcNow);

                    _usageRepo.RecordUsageAsync(usage, CancellationToken.None).Wait();
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

    [RelayCommand]
    private async Task EditMetadata()
    {
        if (SelectedItem is null) return;

        var vm = _viewModelFactory.CreateMetadataEditViewModel(
            SelectedItem.Id,
            SelectedItem.Name,
            SelectedItem.Description,
            SelectedItem.CategoryName,
            SelectedItem.Tags,
            SelectedItem.Status);

        var result = _dialogService.ShowMetadataEdit(vm);
        if (result != true) return;

        await SearchAsync();
    }

    [RelayCommand]
    private async Task CreateDatabaseAsync()
    {
        var name = _dialogService.ShowInputDialog(
            "Новая база данных",
            "Введите название новой базы данных:",
            "Новый каталог");

        if (string.IsNullOrWhiteSpace(name)) return;

        IsLoading = true;
        try
        {
            var db = await _databaseManager.CreateDatabaseAsync(name!.Trim());
            RefreshDatabases();
            SelectedDatabase = Databases.FirstOrDefault(d => d.Id == db.Id);
            StatusMessage = $"База данных \"{db.Name}\" создана";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Ошибка создания БД: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DeleteDatabaseAsync()
    {
        if (SelectedDatabase is null) return;

        var isActive = SelectedDatabase.Id == _databaseManager.GetActiveDatabaseId();
        var databases = _databaseManager.ListDatabases();
        if (isActive && databases.Count <= 1)
        {
            _dialogService.ShowWarning("Удаление БД", "Нельзя удалить единственную базу данных.");
            return;
        }

        var confirm = _dialogService.ShowInputDialog(
            "Удаление базы данных",
            $"Введите \"{SelectedDatabase.Name}\" для подтверждения удаления:",
            "");

        if (confirm != SelectedDatabase.Name) return;

        IsLoading = true;
        try
        {
            var success = await _databaseManager.DeleteDatabaseAsync(SelectedDatabase.Id);
            if (success)
            {
                StatusMessage = $"База данных \"{SelectedDatabase.Name}\" удалена";
                RefreshDatabases();
            }
            else
            {
                StatusMessage = "Ошибка удаления базы данных";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Ошибка удаления БД: {ex.Message}";
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
            "Удаление семейства",
            $"Удалить семейство \"{SelectedItem.Name}\" из каталога?");

        if (!confirmed) return;

        IsLoading = true;
        try
        {
            var success = await _writableProvider.DeleteItemAsync(SelectedItem.Id);
            if (success)
            {
                StatusMessage = $"Семейство \"{SelectedItem.Name}\" удалено из каталога";
                await SearchAsync();
            }
            else
            {
                StatusMessage = "Ошибка удаления семейства";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Ошибка удаления: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

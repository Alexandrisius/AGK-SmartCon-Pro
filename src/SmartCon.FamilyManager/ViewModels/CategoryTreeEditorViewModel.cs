using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// ViewModel for the category tree editor dialog.
/// </summary>
public sealed partial class CategoryTreeEditorViewModel : ObservableObject, IObservableRequestClose
{
    private readonly ICategoryRepository _categoryRepository;
    private readonly IFamilyManagerDialogService _dialogService;

    [ObservableProperty] private ObservableCollection<CategoryNodeViewModel> _rootNodes = [];
    [ObservableProperty] private CategoryNodeViewModel? _selectedNode;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public event Action<bool?>? RequestClose;

    public CategoryTreeEditorViewModel(
        ICategoryRepository categoryRepository,
        IFamilyManagerDialogService dialogService)
    {
        _categoryRepository = categoryRepository;
        _dialogService = dialogService;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await LoadTreeAsync(ct);
    }

    private async Task LoadTreeAsync(CancellationToken ct = default)
    {
        IReadOnlyList<CategoryNode> nodes = [];
        try
        {
            nodes = await _categoryRepository.GetAllAsync(ct);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"CategoryTreeEditor GetAllAsync failed: {ex.Message}");
        }

        IReadOnlyDictionary<string, int> familyCounts = new Dictionary<string, int>();
        try
        {
            familyCounts = await _categoryRepository.GetAllFamilyCountsAsync(ct);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"CategoryTreeEditor GetAllFamilyCountsAsync failed: {ex.Message}");
        }

        var tree = new CategoryTree(nodes);
        RootNodes = BuildTreeNodes(tree, null, familyCounts);
    }

    private static ObservableCollection<CategoryNodeViewModel> BuildTreeNodes(
        CategoryTree tree, string? parentId, IReadOnlyDictionary<string, int> familyCounts)
    {
        var result = new ObservableCollection<CategoryNodeViewModel>();
        foreach (var node in tree.GetChildren(parentId))
        {
            var vm = new CategoryNodeViewModel(node);
            foreach (var child in BuildTreeNodes(tree, node.Id, familyCounts))
                vm.Children.Add(child);
            vm.FamilyCount = ComputeRecursiveCount(vm, familyCounts);
            result.Add(vm);
        }
        return result;
    }

    internal static int ComputeRecursiveCount(CategoryNodeViewModel vm, IReadOnlyDictionary<string, int> familyCounts)
    {
        var total = familyCounts.TryGetValue(vm.CategoryId, out var c) ? c : 0;
        foreach (var child in vm.Children)
        {
            if (child is CategoryNodeViewModel catChild)
                total += ComputeRecursiveCount(catChild, familyCounts);
        }
        return total;
    }

    [RelayCommand]
    private async Task AddRootAsync()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_AddCategory) ?? "Add Category";
        var prompt = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_CategoryName) ?? "Category name:";
        var name = _dialogService.ShowInputDialog(title, prompt);
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var sortOrder = RootNodes.Count;
            var node = await _categoryRepository.AddAsync(name!, null, sortOrder);
            var vm = new CategoryNodeViewModel(node);
            RootNodes.Add(vm);
            SelectedNode = vm;
            vm.IsSelected = true;
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private async Task AddChildAsync()
    {
        if (SelectedNode is not CategoryNodeViewModel parent) return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_AddSubcategory) ?? "Add Subcategory";
        var prompt = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_SubcategoryName) ?? "Subcategory name:";
        var name = _dialogService.ShowInputDialog(title, prompt);
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var sortOrder = parent.Children.Count;
            var node = await _categoryRepository.AddAsync(name!, parent.CategoryId, sortOrder);
            var childVm = new CategoryNodeViewModel(node);
            parent.Children.Add(childVm);
            parent.IsExpanded = true;
            SelectedNode = childVm;
            childVm.IsSelected = true;
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_Rename) ?? "Rename Category";
        var prompt = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_NewName) ?? "New name:";
        var newName = _dialogService.ShowInputDialog(title, prompt, node.DisplayName);
        if (string.IsNullOrWhiteSpace(newName) || newName == node.DisplayName) return;

        try
        {
            var updated = await _categoryRepository.RenameAsync(node.CategoryId, newName!);
            if (updated is not null)
            {
                node.DisplayName = updated.Name;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;

        try
        {
            var count = await _categoryRepository.GetFamilyCountAsync(node.CategoryId);
            var noCat = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "No category";
            var message = count > 0
                ? string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_DeleteConfirm) ?? "Delete \"{0}\"? {1} families will be moved to \"{2}\".", node.DisplayName, count, noCat)
                : string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_DeleteEmpty) ?? "Delete \"{0}\"?", node.DisplayName);

            var delTitle = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_DeleteCategory) ?? "Delete Category";
            if (!_dialogService.ShowConfirmation(delTitle, message)) return;

            var success = await _categoryRepository.DeleteAsync(node.CategoryId);
            if (success)
            {
                RemoveNodeFromTree(node.CategoryId);
                StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_Deleted) ?? "Deleted: {0}", node.DisplayName);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}", ex.Message);
        }
    }

    private void RemoveNodeFromTree(string categoryId)
    {
        for (var i = 0; i < RootNodes.Count; i++)
        {
            if (RootNodes[i].CategoryId == categoryId)
            {
                RootNodes.RemoveAt(i);
                return;
            }
            if (TryRemoveNode(RootNodes[i].Children, categoryId))
                return;
        }
    }

    internal static bool TryRemoveNode(ObservableCollection<CatalogTreeNodeViewModel> nodes, string categoryId)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is CategoryNodeViewModel cat && cat.CategoryId == categoryId)
            {
                nodes.RemoveAt(i);
                return true;
            }
            if (TryRemoveNode(nodes[i].Children, categoryId))
                return true;
        }
        return false;
    }

    [RelayCommand]
    private async Task MoveNodeAsync((CategoryNodeViewModel Node, CategoryNodeViewModel? NewParent, int SortOrder) args)
    {
        var (node, newParent, sortOrder) = args;
        var newParentId = newParent?.CategoryId;
        await _categoryRepository.MoveAsync(node.CategoryId, newParentId, sortOrder);
        await LoadTreeAsync();
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_ImportTree) ?? "Import Category Tree";
        var path = _dialogService.ShowOpenJsonDialog(title);
        if (path is null) return;

        try
        {
            var json = await Task.Run(() => File.ReadAllText(path));
            var data = JsonSerializer.Deserialize<CategoryTreeImportData>(json);
            if (data?.Categories is null) return;

            var flatNodes = FlattenImportData(data.Categories, null, 0);
            await _categoryRepository.ReplaceAllAsync(flatNodes);
            await LoadTreeAsync();
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_Imported) ?? "Imported {0} categories", flatNodes.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_ExportTree) ?? "Export Category Tree";
        var path = _dialogService.ShowSaveJsonDialog(title, "categories");
        if (path is null) return;

        try
        {
            var nodes = await _categoryRepository.GetAllAsync();
            var tree = new CategoryTree(nodes);
            var exportData = BuildExportTree(tree, null);
            var json = JsonSerializer.Serialize(new CategoryTreeImportData { Categories = exportData }, new JsonSerializerOptions { WriteIndented = true });
            await Task.Run(() => File.WriteAllText(path, json));
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_Exported) ?? "Exported to {0}", path);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}", ex.Message);
        }
    }

    internal static List<CategoryTreeImportData.CategoryImportItem> BuildExportTree(CategoryTree tree, string? parentId)
    {
        var result = new List<CategoryTreeImportData.CategoryImportItem>();
        foreach (var node in tree.GetChildren(parentId))
        {
            result.Add(new CategoryTreeImportData.CategoryImportItem
            {
                Name = node.Name,
                Children = BuildExportTree(tree, node.Id)
            });
        }
        return result;
    }

    internal static List<CategoryNode> FlattenImportData(List<CategoryTreeImportData.CategoryImportItem> items, string? parentId, int startOrder)
    {
        var result = new List<CategoryNode>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var id = Guid.NewGuid().ToString();
            var path = parentId is null ? item.Name : $"{parentId}/{item.Name}";
            result.Add(new CategoryNode(id, item.Name, parentId, startOrder + i, path, DateTimeOffset.UtcNow));
            if (item.Children?.Count > 0)
            {
                result.AddRange(FlattenImportData(item.Children, id, 0));
            }
        }
        return result;
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke(true);
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class CategoryPickerViewModel : ObservableObject, IObservableRequestClose
{
    private readonly ICategoryRepository _categoryRepository;

    [ObservableProperty] private ObservableCollection<CategoryNodeViewModel> _rootNodes = [];
    [ObservableProperty] private CategoryNodeViewModel? _selectedNode;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedPath = string.Empty;
    [ObservableProperty] private string? _selectedCategoryId;
    [ObservableProperty] private bool _allowClear;

    public event Action<bool?>? RequestClose;

    public CategoryPickerViewModel(ICategoryRepository categoryRepository, bool allowClear = true)
    {
        _categoryRepository = categoryRepository;
        _allowClear = allowClear;
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
        catch
        {
        }

        var tree = new CategoryTree(nodes);

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            RootNodes = BuildNodes(tree, null, null);
        }
        else
        {
            var filtered = FilterNodesWithAncestors(tree, SearchText);
            RootNodes = BuildNodes(tree, null, filtered);
        }
    }

    private static List<CategoryNode> FilterNodesWithAncestors(CategoryTree tree, string search)
    {
        var allNodes = tree.GetAllNodes();
        var normalizedSearch = search.ToLowerInvariant();
        var byId = allNodes.ToDictionary(n => n.Id);

        var result = new HashSet<CategoryNode>();
        foreach (var node in allNodes.Where(n => n.Name.ToLowerInvariant().Contains(normalizedSearch)))
        {
            result.Add(node);
            var parentId = node.ParentId;
            while (parentId is not null && byId.TryGetValue(parentId, out var parent))
            {
                result.Add(parent);
                parentId = parent.ParentId;
            }
        }
        return result.ToList();
    }

    private static ObservableCollection<CategoryNodeViewModel> BuildNodes(CategoryTree tree, string? parentId, List<CategoryNode>? filter = null)
    {
        var children = filter != null
            ? filter.Where(n => n.ParentId == parentId).ToList()
            : tree.GetChildren(parentId).ToList();

        var result = new ObservableCollection<CategoryNodeViewModel>();
        foreach (var node in children)
        {
            var vm = new CategoryNodeViewModel(node);
            var childNodes = BuildNodes(tree, node.Id, filter);
            foreach (var child in childNodes) vm.Children.Add(child);
            if (vm.Children.Count > 0) vm.IsExpanded = true;
            result.Add(vm);
        }
        return result;
    }

    partial void OnSelectedNodeChanged(CategoryNodeViewModel? value)
    {
        if (value is CategoryNodeViewModel cat)
        {
            SelectedCategoryId = cat.CategoryId;
            SelectedPath = cat.FullPath;
        }
        else
        {
            SelectedCategoryId = null;
            SelectedPath = string.Empty;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        FireAndForget(() => LoadTreeAsync());
    }

    [RelayCommand]
    private void Select()
    {
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Clear()
    {
        SelectedCategoryId = null;
        SelectedPath = string.Empty;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);

    private static async void FireAndForget(Func<Task> taskFactory)
    {
        try { await taskFactory(); } catch { }
    }
}

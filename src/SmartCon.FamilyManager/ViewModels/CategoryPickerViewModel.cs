using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Common;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// ViewModel for the category picker dialog.
/// </summary>
public sealed partial class CategoryPickerViewModel : ObservableObject, IObservableRequestClose
{
    private readonly ICategoryRepository _categoryRepository;

    [ObservableProperty] private ObservableCollection<CategoryNodeViewModel> _rootNodes = [];
    [ObservableProperty] private CategoryNodeViewModel? _selectedNode;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedPath = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelectCommand))]
    private string? _selectedCategoryId;
    [ObservableProperty] private bool _allowClear;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubtitle))]
    private string _subtitle = string.Empty;

    /// <summary>
    /// #241: when set, the picker shows ONLY these categories (plus their
    /// ancestors) — the auto-assignment recommendation mode. The user's
    /// search still applies within the recommended set.
    /// </summary>
    private IReadOnlyCollection<string>? _recommendedCategoryIds;

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

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

    /// <summary>
    /// #241: recommendation mode — only the listed categories (with their
    /// ancestors) are shown, under the explaining subtitle.
    /// </summary>
    public async Task InitializeRecommendedAsync(
        IReadOnlyList<string> categoryIds,
        string subtitle,
        CancellationToken ct = default)
    {
        _recommendedCategoryIds = new HashSet<string>(categoryIds, StringComparer.Ordinal);
        Subtitle = subtitle;
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
            SmartConLogger.Warn($"CategoryPicker.LoadTreeAsync: failed: {ex.Message} [Action: закройте и откройте picker снова; проверьте БД каталога]");
        }

        var tree = new CategoryTree(nodes);

        List<CategoryNode>? filter = null;
        if (_recommendedCategoryIds is not null)
        {
            filter = FilterNodesWithIds(tree, _recommendedCategoryIds);
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var searchMatches = FilterNodesWithAncestors(tree, SearchText);
                filter = filter.Where(searchMatches.Contains).ToList();
            }
        }
        else if (!string.IsNullOrWhiteSpace(SearchText))
        {
            filter = FilterNodesWithAncestors(tree, SearchText);
        }

        RootNodes = BuildNodes(tree, null, filter);
    }

    /// <summary>#241: the recommended categories plus all their ancestors
    /// (the tree stays navigable top-down).</summary>
    internal static List<CategoryNode> FilterNodesWithIds(CategoryTree tree, IReadOnlyCollection<string> categoryIds)
    {
        var allNodes = tree.GetAllNodes();
        var byId = allNodes.ToDictionary(n => n.Id);

        var result = new HashSet<CategoryNode>();
        foreach (var node in allNodes.Where(n => categoryIds.Contains(n.Id)))
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

    internal static List<CategoryNode> FilterNodesWithAncestors(CategoryTree tree, string search)
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
        // OnSearchTextChanged fires on the UI thread (PropertyChanged setter),
        // so Dispatcher.CurrentDispatcher returns the UI thread dispatcher.
        var dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? Dispatcher.CurrentDispatcher;
        if (!dispatcher.HasShutdownStarted)
        {
            _ = dispatcher.InvokeAsync(() => LoadTreeAsync());
        }
    }

    [RelayCommand]
    private void OnSelectedItemChanged(object? selectedItem)
    {
        SelectedNode = selectedItem as CategoryNodeViewModel;
    }

    [RelayCommand(CanExecute = nameof(CanSelect))]
    private void Select()
    {
        RequestClose?.Invoke(true);
    }

    /// <summary>#241: in the recommendation mode only the RECOMMENDED
    /// categories are selectable — ancestors are navigation scaffolding,
    /// picking one would keep the conflict icon lit.</summary>
    private bool CanSelect() =>
        _recommendedCategoryIds is null
        || (SelectedCategoryId is not null && _recommendedCategoryIds.Contains(SelectedCategoryId));

    [RelayCommand]
    private void Clear()
    {
        SelectedCategoryId = null;
        SelectedPath = string.Empty;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);

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
                SmartConLogger.Error($"FireAndForget({operationName}): {ex.GetBaseException()}");
            }
        });
    }
}

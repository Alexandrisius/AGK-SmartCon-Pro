using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class CategoryTreeEditorViewModel : ObservableObject, IObservableRequestClose, ICloseAwareViewModel
{
    private readonly ICategoryRepository _categoryRepository;
    private readonly IFamilyManagerDialogService _dialogService;
    private readonly IAttributeDefinitionRepository _attributeDefRepository;
    private readonly ICategoryAttributeBindingService _bindingService;
    private readonly IFamilyMetadataPackageService _packageService;
    private readonly IFamilyManagerViewModelFactory _viewModelFactory;
    private List<AttributeListItemViewModel> _allAttributeItems = [];
    private readonly Dictionary<string, bool> _bindingChanges = new();
    private readonly List<CategoryNodeViewModel> _pendingCategoryDeletions = [];
    private FamilyMetadataPackage? _pendingImportPackage;

    [ObservableProperty] private ObservableCollection<CategoryNodeViewModel> _rootNodes = [];
    [ObservableProperty] private CategoryNodeViewModel? _selectedNode;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private ObservableCollection<AttributeListItemViewModel> _attributeItems = [];
    [ObservableProperty] private string _attributeFilterText = string.Empty;
    [ObservableProperty] private string? _selectedGroupFilter;
    [ObservableProperty] private ObservableCollection<string> _availableGroups = [];
    [ObservableProperty] private string _selectedCategoryPath = string.Empty;
    [ObservableProperty] private bool _hasSelectedCategory;
    [ObservableProperty] private bool _hasUnsavedChanges;
    [ObservableProperty] private bool _isSaved;

    public event Action<bool?>? RequestClose;
    public event Action? Saved;

    public CategoryTreeEditorViewModel(
        ICategoryRepository categoryRepository,
        IFamilyManagerDialogService dialogService,
        IAttributeDefinitionRepository attributeDefRepository,
        ICategoryAttributeBindingService bindingService,
        IFamilyMetadataPackageService packageService,
        IFamilyManagerViewModelFactory viewModelFactory)
    {
        _categoryRepository = categoryRepository;
        _dialogService = dialogService;
        _attributeDefRepository = attributeDefRepository;
        _bindingService = bindingService;
        _packageService = packageService;
        _viewModelFactory = viewModelFactory;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await LoadTreeAsync(ct);
    }

    partial void OnSelectedNodeChanged(CategoryNodeViewModel? value)
    {
        HasSelectedCategory = value is not null;
        if (value is not null)
        {
            SelectedCategoryPath = BuildCategoryPath(value);
            SmartConLogger.Freeze("CategoryTreeEditor: FireAndForgetAsync.LoadAttributesForCategoryAsync");
            SmartConLogger.FreezeThreadPool("CategoryTreeEditor.Before.FireAndForgetAsync");
            _ = FireAndForgetAsync(() => LoadAttributesForCategoryAsync(value));
        }
        else
        {
            SelectedCategoryPath = string.Empty;
            AttributeItems = [];
            AvailableGroups = [];
            _allAttributeItems = [];
        }
    }

    partial void OnAttributeFilterTextChanged(string value) => ApplyAttributeFilter();

    partial void OnSelectedGroupFilterChanged(string? value) => ApplyAttributeFilter();

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

    private async Task LoadAttributesForCategoryAsync(CategoryNodeViewModel? categoryNode)
    {
        if (categoryNode is null)
        {
            HasSelectedCategory = false;
            AttributeItems = [];
            AvailableGroups = [];
            _allAttributeItems = [];
            return;
        }

        try
        {
            var categoryId = categoryNode.CategoryId;
            var allDefs = await _attributeDefRepository.GetAllAsync();
            var categories = await _categoryRepository.GetAllAsync();
            var categoryNameById = categories.ToDictionary(c => c.Id, c => c.Name);

            // Collect effective attributes (DB + draft tree)
            List<EffectiveCategoryAttribute> effectiveAttrs;
            List<CategoryAttributeBinding> directBindings;
            
            // Build effective attrs with draft awareness for ALL categories
            effectiveAttrs = await GetDraftEffectiveAttributesAsync(categoryNode, categoryNameById);
            directBindings = categoryNode.IsNew
                ? GetDraftDirectBindings(categoryNode)
                : [..await _bindingService.GetDirectBindingsAsync(categoryId)];

            var effectiveByAttrId = effectiveAttrs.ToDictionary(e => e.AttributeId);
            var bindingByAttrId = directBindings.ToDictionary(b => b.AttributeId);

            var items = new List<AttributeListItemViewModel>();
            foreach (var def in allDefs.Where(d => d.IsActive))
            {
                effectiveByAttrId.TryGetValue(def.Id, out var effective);
                bindingByAttrId.TryGetValue(def.Id, out var binding);

                var sourceName = effective?.SourceCategoryId is not null
                    && categoryNameById.TryGetValue(effective.SourceCategoryId, out var catName)
                    ? catName : null;

                var isBound = effective is not null;
                var key = $"{categoryId}:{def.Id}";
                if (_bindingChanges.TryGetValue(key, out var changedBound))
                {
                    isBound = changedBound;
                }

                items.Add(new AttributeListItemViewModel
                {
                    AttributeId = def.Id,
                    Name = def.Name,
                    Group = def.Group,
                    IsBound = isBound,
                    OriginalIsBound = effective is not null,
                    IsInherited = effective?.IsInherited ?? false,
                    SourceCategoryName = sourceName,
                    BindingId = binding?.Id,
                    IsEnabled = effective?.IsEnabled ?? true,
                    Parent = this
                });
            }

            _allAttributeItems = items;

            var groups = allDefs
                .Where(d => d.IsActive && d.Group is not null)
                .Select(d => d.Group!)
                .Distinct()
                .OrderBy(g => g)
                .ToList();

            var allGroupsLabel = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_AllGroups) ?? "Все атрибуты";
            var allGroups = new List<string> { allGroupsLabel };
            allGroups.AddRange(groups);

            AvailableGroups = new ObservableCollection<string>(allGroups);
            SelectedGroupFilter = allGroupsLabel;
            AttributeFilterText = string.Empty;
            ApplyAttributeFilter();
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"LoadAttributesForCategoryAsync failed: {ex.Message}");
        }
    }

    internal void HandleBindingToggle(AttributeListItemViewModel item, bool shouldBeBound)
    {
        if (SelectedNode is null) return;
        var categoryId = SelectedNode.CategoryId;
        var attributeId = item.AttributeId;

        var key = $"{categoryId}:{attributeId}";
        _bindingChanges[key] = shouldBeBound;

        item.IsBound = shouldBeBound;
        item.IsDirty = true;
        UpdateHasUnsavedChanges();
        ApplyAttributeFilter();
    }

    private void UpdateHasUnsavedChanges()
    {
        var allNodes = FlattenNodes(RootNodes);
        HasUnsavedChanges = _pendingCategoryDeletions.Count > 0
                         || _bindingChanges.Count > 0
                         || allNodes.Any(n => n.IsNew || n.IsDirty);
    }

    private void ApplyAttributeFilter()
    {
        var filtered = _allAttributeItems.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(AttributeFilterText))
        {
            var filter = AttributeFilterText.Trim().ToUpperInvariant();
            filtered = filtered.Where(x =>
                x.Name.ToUpperInvariant().Contains(filter) ||
                (x.Group is not null && x.Group.ToUpperInvariant().Contains(filter)));
        }

        var allGroupsLabel = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_AllGroups) ?? "Все атрибуты";
        if (!string.IsNullOrWhiteSpace(SelectedGroupFilter) && SelectedGroupFilter != allGroupsLabel)
        {
            filtered = filtered.Where(x => x.Group == SelectedGroupFilter);
        }

        AttributeItems = new ObservableCollection<AttributeListItemViewModel>(filtered);
    }

    [RelayCommand]
    private async Task OpenAttributeLibraryAsync()
    {
        var libraryVm = _viewModelFactory.CreateAttributeLibraryViewModel();
        await libraryVm.InitializeAsync();
        _dialogService.ShowAttributeLibrary(libraryVm);

        if (SelectedNode is not null)
            await LoadAttributesForCategoryAsync(SelectedNode);
    }

    [RelayCommand]
    private async Task OkAsync()
    {
        try
        {
            foreach (var node in _pendingCategoryDeletions.Where(n => !n.IsNew))
            {
                await _categoryRepository.DeleteAsync(node.CategoryId);
            }
            _pendingCategoryDeletions.Clear();

            var allNodes = FlattenNodes(RootNodes);

            var tempToRealId = new Dictionary<string, string>();

            foreach (var node in allNodes.Where(n => n.IsNew))
            {
                var realParentId = node.ParentId is not null && tempToRealId.TryGetValue(node.ParentId, out var mappedParent)
                    ? mappedParent
                    : node.ParentId;

                var created = await _categoryRepository.AddAsync(node.DisplayName, realParentId, node.SortOrder);
                tempToRealId[node.CategoryId] = created.Id;
                node.CategoryId = created.Id;
                node.ParentId = realParentId;
                node.IsNew = false;
                node.IsDirty = false;
            }

            foreach (var node in allNodes.Where(n => n.IsDirty && !n.IsNew))
            {
                if (node.DisplayName != node.OriginalName)
                {
                    await _categoryRepository.RenameAsync(node.CategoryId, node.DisplayName);
                    node.OriginalName = node.DisplayName;
                }
                if (node.ParentId != node.OriginalParentId || node.SortOrder != node.OriginalSortOrder)
                {
                    await _categoryRepository.MoveAsync(node.CategoryId, node.ParentId, node.SortOrder);
                    node.OriginalParentId = node.ParentId;
                    node.OriginalSortOrder = node.SortOrder;
                }
                node.IsDirty = false;
            }

            var resolvedBindings = new Dictionary<string, bool>();
            foreach (var change in _bindingChanges)
            {
                var parts = change.Key.Split(new[] { ':' }, 2);
                if (parts.Length != 2) continue;
                var categoryId = parts[0];
                var attributeId = parts[1];

                var realCategoryId = tempToRealId.TryGetValue(categoryId, out var mappedId)
                    ? mappedId
                    : categoryId;

                var key = $"{realCategoryId}:{attributeId}";
                resolvedBindings[key] = change.Value;
            }
            _bindingChanges.Clear();

            foreach (var change in resolvedBindings)
            {
                var parts = change.Key.Split(new[] { ':' }, 2);
                if (parts.Length != 2) continue;
                var categoryId = parts[0];
                var attributeId = parts[1];
                var shouldBeBound = change.Value;

                var existing = await _bindingService.GetDirectBindingsAsync(categoryId);
                var existingBinding = existing.FirstOrDefault(b => b.AttributeId == attributeId);

                if (shouldBeBound && existingBinding is null)
                {
                    var sortOrder = existing.Count;
                    await _bindingService.CreateBindingAsync(categoryId, attributeId, sortOrder);
                }
                else if (!shouldBeBound && existingBinding is not null)
                {
                    await _bindingService.DeleteBindingAsync(existingBinding.Id);
                }
            }

            if (_pendingImportPackage is not null)
            {
                var packageWithoutCategories = new FamilyMetadataPackage
                {
                    Format = _pendingImportPackage.Format,
                    Version = _pendingImportPackage.Version,
                    ExportedAtUtc = _pendingImportPackage.ExportedAtUtc,
                    Sections = new FamilyMetadataPackageSections
                    {
                        Categories = false,
                        Attributes = _pendingImportPackage.Sections.Attributes,
                        Bindings = _pendingImportPackage.Sections.Bindings
                    },
                    Categories = [],
                    Attributes = _pendingImportPackage.Attributes,
                    Bindings = _pendingImportPackage.Bindings
                };
                await _packageService.ImportAsync(packageWithoutCategories);
                _pendingImportPackage = null;
            }

            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_Saved) ?? "Saved";
            HasUnsavedChanges = false;
            IsSaved = true;
            await Task.Delay(500);
            IsSaved = false;
            StatusMessage = string.Empty;
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}", ex.Message);
        }
    }

    private static List<CategoryNodeViewModel> FlattenNodes(ObservableCollection<CategoryNodeViewModel> nodes)
    {
        var result = new List<CategoryNodeViewModel>();
        foreach (var node in nodes)
        {
            result.Add(node);
            result.AddRange(FlattenNodes(node.Children));
        }
        return result;
    }

    private static List<CategoryNodeViewModel> FlattenNodes(ObservableCollection<CatalogTreeNodeViewModel> nodes)
    {
        var result = new List<CategoryNodeViewModel>();
        foreach (var node in nodes)
        {
            if (node is CategoryNodeViewModel cat)
            {
                result.Add(cat);
                result.AddRange(FlattenNodes(cat.Children));
            }
        }
        return result;
    }

    public bool? ConfirmUnsavedChanges()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_UnsavedChangesTitle) ?? "Unsaved Changes";
        var message = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_UnsavedChangesMessage) ?? "You have unsaved changes. Save before closing?";
        var result = _dialogService.ShowYesNoCancel(title, message);
        return result switch
        {
            Core.Services.Interfaces.DialogResult.Yes => true,
            Core.Services.Interfaces.DialogResult.No => false,
            _ => null
        };
    }

    public void ConfirmClose(CloseConfirmationArgs args)
    {
        if (!HasUnsavedChanges)
            return;

        var result = ConfirmUnsavedChanges();
        if (result is null)
        {
            args.Cancel = true;
            return;
        }

        if (result == true)
        {
            args.DeferredAction = () => OkCommand.Execute(null);
        }

        args.DialogResult = false;
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);

    private static async Task FireAndForgetAsync(Func<Task> taskFactory)
    {
        try
        {
            await taskFactory();
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"FireAndForget: {ex.Message}");
        }
    }
}

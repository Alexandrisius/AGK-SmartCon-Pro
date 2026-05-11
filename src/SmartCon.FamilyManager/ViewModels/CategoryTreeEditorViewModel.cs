using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    private async Task<List<EffectiveCategoryAttribute>> GetDraftEffectiveAttributesAsync(
        CategoryNodeViewModel node, Dictionary<string, string> categoryNameById)
    {
        var result = new Dictionary<string, EffectiveCategoryAttribute>();

        // Step 1: Load base effective attrs
        if (!node.IsNew)
        {
            var dbEffective = await _bindingService.GetEffectiveAttributesAsync(node.CategoryId);
            foreach (var attr in dbEffective)
            {
                result[attr.AttributeId] = attr;
            }

            // Apply ALL ancestor draft changes recursively
            await ApplyAncestorDraftChangesAsync(node.ParentId, result);
        }
        else if (node.ParentId is not null)
        {
            // Draft node: inherit from parent recursively with draft awareness
            var parent = FindNodeById(RootNodes, node.ParentId);
            if (parent is not null)
            {
                var parentEffective = await GetDraftEffectiveAttributesAsync(parent, categoryNameById);
                foreach (var attr in parentEffective)
                {
                    result[attr.AttributeId] = attr with { IsInherited = true };
                }
            }
            else
            {
                var dbEffective = await _bindingService.GetEffectiveAttributesAsync(node.ParentId);
                foreach (var attr in dbEffective)
                {
                    result[attr.AttributeId] = attr with { IsInherited = true };
                }
            }
        }

        // Step 2: Apply own draft changes (add/remove bindings)
        var ownChanges = _bindingChanges
            .Where(c =>
            {
                var parts = c.Key.Split(new[] { ':' }, 2);
                return parts.Length == 2 && parts[0] == node.CategoryId;
            })
            .ToList();

        foreach (var change in ownChanges)
        {
            var parts = change.Key.Split(new[] { ':' }, 2);
            var attrId = parts[1];

            if (change.Value)
            {
                // Added binding
                if (!result.ContainsKey(attrId))
                {
                    var attrDef = await _attributeDefRepository.GetByIdAsync(attrId);
                    if (attrDef is not null)
                    {
                        result[attrId] = new EffectiveCategoryAttribute(
                            attrId,
                            attrDef.Name,
                            attrDef.Group,
                            result.Count,
                            true,
                            false, // direct binding, not inherited
                            node.CategoryId);
                    }
                }
            }
            else
            {
                // Removed binding
                result.Remove(attrId);
            }
        }

        return [..result.Values];
    }

    private List<CategoryAttributeBinding> GetDraftDirectBindings(CategoryNodeViewModel node)
    {
        var result = new List<CategoryAttributeBinding>();

        foreach (var change in _bindingChanges)
        {
            var parts = change.Key.Split(new[] { ':' }, 2);
            if (parts.Length != 2 || parts[0] != node.CategoryId) continue;

            if (change.Value)
            {
                result.Add(new CategoryAttributeBinding(
                    Guid.NewGuid().ToString(),
                    node.CategoryId,
                    parts[1],
                    result.Count,
                    true));
            }
        }

        return result;
    }

    private async Task ApplyAncestorDraftChangesAsync(
        string? parentId,
        Dictionary<string, EffectiveCategoryAttribute> result)
    {
        if (parentId is null) return;

        // Check if this ancestor has draft changes
        var parentChanges = _bindingChanges
            .Where(c => c.Key.StartsWith($"{parentId}:"))
            .ToList();

        foreach (var change in parentChanges)
        {
            var parts = change.Key.Split(new[] { ':' }, 2);
            if (parts.Length != 2) continue;
            var attrId = parts[1];
            var isBound = change.Value;

            if (isBound && !result.ContainsKey(attrId))
            {
                var attrDef = await _attributeDefRepository.GetByIdAsync(attrId);
                if (attrDef is not null)
                {
                    result[attrId] = new EffectiveCategoryAttribute(
                        attrId,
                        attrDef.Name,
                        attrDef.Group,
                        result.Count,
                        true,
                        true,
                        parentId);
                }
            }
            else if (!isBound && result.ContainsKey(attrId) && result[attrId].SourceCategoryId == parentId)
            {
                result.Remove(attrId);
            }
        }

        // Recursively apply grandparent's draft changes
        var parent = FindNodeById(RootNodes, parentId);
        if (parent is not null)
        {
            await ApplyAncestorDraftChangesAsync(parent.ParentId, result);
        }
        else
        {
            // Parent only in DB - get its parentId from DB
            var parentNode = await _categoryRepository.GetByIdAsync(parentId);
            await ApplyAncestorDraftChangesAsync(parentNode?.ParentId, result);
        }
    }

    private static CategoryNodeViewModel? FindNodeById(ObservableCollection<CategoryNodeViewModel> nodes, string categoryId)
    {
        foreach (var node in nodes)
        {
            if (node.CategoryId == categoryId) return node;
            var found = FindNodeById(node.Children, categoryId);
            if (found is not null) return found;
        }
        return null;
    }

    private static CategoryNodeViewModel? FindNodeById(ObservableCollection<CatalogTreeNodeViewModel> nodes, string categoryId)
    {
        foreach (var node in nodes)
        {
            if (node is CategoryNodeViewModel cat && cat.CategoryId == categoryId) return cat;
            var found = FindNodeById(node.Children, categoryId);
            if (found is not null) return found;
        }
        return null;
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
    private async Task ExportCategoriesAsync()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_ExportTree) ?? "Export Categories";
        var path = _dialogService.ShowSaveJsonDialog(title, "categories");
        if (path is null) return;

        try
        {
            var package = await _packageService.ExportCategoriesAsync();
            var json = JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true });
            await Task.Run(() => File.WriteAllText(path, json));
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_Exported) ?? "Exported to {0}", path);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportAttributesAsync()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_ExportAttrs) ?? "Export Attributes";
        var path = _dialogService.ShowSaveJsonDialog(title, "attributes");
        if (path is null) return;

        try
        {
            var package = await _packageService.ExportAttributesAsync();
            var json = JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true });
            await Task.Run(() => File.WriteAllText(path, json));
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_Exported) ?? "Exported to {0}", path);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportFullAsync()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_ExportFull) ?? "Export Full Package";
        var path = _dialogService.ShowSaveJsonDialog(title, "smartcon-metadata");
        if (path is null) return;

        try
        {
            var package = await _packageService.ExportFullAsync();
            var json = JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true });
            await Task.Run(() => File.WriteAllText(path, json));
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_Exported) ?? "Exported to {0}", path);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void ImportFromJson()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_ImportTree) ?? "Import Metadata";
        var path = _dialogService.ShowOpenJsonDialog(title);
        if (path is null) return;

        try
        {
            var json = File.ReadAllText(path);
            var package = JsonSerializer.Deserialize<FamilyMetadataPackage>(json);
            if (package is null) return;

            var importedNodes = new List<CategoryNodeViewModel>();
            foreach (var cat in package.Categories)
            {
                importedNodes.AddRange(ImportCategoryNode(cat, null, 0));
            }

            RootNodes = new ObservableCollection<CategoryNodeViewModel>(importedNodes);
            _bindingChanges.Clear();
            _pendingImportPackage = package;
            SelectedNode = null;
            UpdateHasUnsavedChanges();
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_CTE_Imported) ?? "Imported {0} categories",
                importedNodes.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}", ex.Message);
        }
    }

    private static List<CategoryNodeViewModel> ImportCategoryNode(FamilyMetadataCategoryNode node, string? parentId, int sortOrder)
    {
        var result = new List<CategoryNodeViewModel>();
        var categoryId = Guid.NewGuid().ToString();
        var vm = new CategoryNodeViewModel(categoryId, node.Name, parentId, node.Name)
        {
            SortOrder = sortOrder,
            OriginalSortOrder = sortOrder,
            IsNew = true,
            IsDirty = true
        };
        result.Add(vm);

        for (int i = 0; i < node.Children.Count; i++)
        {
            var children = ImportCategoryNode(node.Children[i], categoryId, i);
            foreach (var child in children)
            {
                vm.Children.Add(child);
            }
        }

        return result;
    }

    [RelayCommand]
    private void AddRoot()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_AddCategory) ?? "Add Category";
        var prompt = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_CategoryName) ?? "Category name:";
        var name = _dialogService.ShowInputDialog(title, prompt);
        if (string.IsNullOrWhiteSpace(name)) return;

        var sortOrder = RootNodes.Count;
        var categoryId = Guid.NewGuid().ToString();
        var vm = new CategoryNodeViewModel(categoryId, name!, null, name!)
        {
            SortOrder = sortOrder,
            OriginalSortOrder = sortOrder,
            IsNew = true,
            IsDirty = true
        };
        RootNodes.Add(vm);
        SelectedNode = vm;
        vm.IsSelected = true;
        UpdateHasUnsavedChanges();
    }

    [RelayCommand]
    private void AddChild()
    {
        if (SelectedNode is not CategoryNodeViewModel parent) return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_AddSubcategory) ?? "Add Subcategory";
        var prompt = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_SubcategoryName) ?? "Subcategory name:";
        var name = _dialogService.ShowInputDialog(title, prompt);
        if (string.IsNullOrWhiteSpace(name)) return;

        var sortOrder = parent.Children.Count;
        var categoryId = Guid.NewGuid().ToString();
        var fullPath = $"{parent.FullPath}/{name}";
        var childVm = new CategoryNodeViewModel(categoryId, name!, parent.CategoryId, fullPath)
        {
            SortOrder = sortOrder,
            OriginalSortOrder = sortOrder,
            IsNew = true,
            IsDirty = true
        };
        parent.Children.Add(childVm);
        parent.IsExpanded = true;
        SelectedNode = childVm;
        childVm.IsSelected = true;
        UpdateHasUnsavedChanges();
    }

    [RelayCommand]
    private void Rename()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_Rename) ?? "Rename Category";
        var prompt = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_NewName) ?? "New name:";
        var newName = _dialogService.ShowInputDialog(title, prompt, node.DisplayName);
        if (string.IsNullOrWhiteSpace(newName) || newName == node.DisplayName) return;

        node.DisplayName = newName!;
        node.IsDirty = true;
        UpdateHasUnsavedChanges();
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;

        var count = node.FamilyCount;
        var noCat = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "No category";
        var message = count > 0
            ? string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_DeleteConfirm) ?? "Delete \"{0}\"? {1} families will be moved to \"{2}\".", node.DisplayName, count, noCat)
            : string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_DeleteEmpty) ?? "Delete \"{0}\"?", node.DisplayName);

        var delTitle = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_DeleteCategory) ?? "Delete Category";
        if (!_dialogService.ShowConfirmation(delTitle, message)) return;

        node.IsDeleted = true;
        _pendingCategoryDeletions.Add(node);
        RemoveNodeFromTree(node.CategoryId);
        SelectedNode = null;
        UpdateHasUnsavedChanges();
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
    private void MoveNode((CategoryNodeViewModel Node, CategoryNodeViewModel? NewParent, int SortOrder) args)
    {
        var (node, newParent, sortOrder) = args;
        node.ParentId = newParent?.CategoryId;
        node.SortOrder = sortOrder;
        node.IsDirty = true;
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

    private string BuildCategoryPath(CategoryNodeViewModel target)
    {
        var path = new List<string>();
        foreach (var root in RootNodes)
        {
            path.Add(root.DisplayName);
            if (root.CategoryId == target.CategoryId) break;
            if (SearchPathInChildren(root.Children, target.CategoryId, path)) break;
            path.RemoveAt(path.Count - 1);
        }
        return string.Join(" > ", path);
    }

    private static bool SearchPathInChildren(IList<CatalogTreeNodeViewModel> children, string targetId, List<string> path)
    {
        foreach (var child in children)
        {
            if (child is not CategoryNodeViewModel cat) continue;
            path.Add(cat.DisplayName);
            if (cat.CategoryId == targetId) return true;
            if (SearchPathInChildren(cat.Children, targetId, path)) return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    [RelayCommand]
    private void OnSelectedItemChanged(object? selectedItem)
    {
        SelectedNode = selectedItem as CategoryNodeViewModel;
    }

    [RelayCommand]
    private void ContextMenuRename()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;
        Rename();
    }

    [RelayCommand]
    private void ContextMenuAddChild()
    {
        if (SelectedNode is not CategoryNodeViewModel parent) return;
        AddChild();
    }

    [RelayCommand]
    private void ContextMenuDelete()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;
        Delete();
    }

    [RelayCommand]
    private async Task SaveAsync()
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
            args.DeferredAction = () => SaveCommand.Execute(null);
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

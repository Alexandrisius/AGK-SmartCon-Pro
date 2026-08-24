using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Models.Metadata;
using SmartCon.FamilyManager.Services;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class CategoryTreeEditorViewModel : ObservableObject, IObservableRequestClose, ICloseAwareViewModel
{
    private readonly ICategoryRepository _categoryRepository;
    private readonly IFamilyManagerDialogService _dialogService;
    private readonly IAttributeDefinitionRepository _attributeDefRepository;
    private readonly ICategoryAttributeBindingService _bindingService;
    private readonly IFamilyManagerMetadataMediator _metadataMediator;
    private readonly IFamilyManagerViewModelFactory _viewModelFactory;
    private readonly IValidationRuleRepository _ruleRepository;
    private readonly IAssignmentRuleRepository _assignmentRuleRepository;
    private List<AttributeListItemViewModel> _allAttributeItems = [];

    [ObservableProperty] private ObservableCollection<CategoryNodeViewModel> _rootNodes = [];
    [ObservableProperty] private CategoryNodeViewModel? _selectedNode;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private ObservableCollection<AttributeListItemViewModel> _attributeItems = [];
    [ObservableProperty] private string _attributeFilterText = string.Empty;
    [ObservableProperty] private string? _selectedGroupFilter;
    [ObservableProperty] private ObservableCollection<string> _availableGroups = [];
    [ObservableProperty] private string _selectedCategoryPath = string.Empty;
    [ObservableProperty] private bool _hasSelectedCategory;

    public event Action<bool?>? RequestClose;

    public CategoryTreeEditorViewModel(
        ICategoryRepository categoryRepository,
        IFamilyManagerDialogService dialogService,
        IAttributeDefinitionRepository attributeDefRepository,
        ICategoryAttributeBindingService bindingService,
        IFamilyManagerMetadataMediator metadataMediator,
        IFamilyManagerViewModelFactory viewModelFactory,
        IValidationRuleRepository ruleRepository,
        IAssignmentRuleRepository assignmentRuleRepository)
    {
        _categoryRepository = categoryRepository;
        _dialogService = dialogService;
        _attributeDefRepository = attributeDefRepository;
        _bindingService = bindingService;
        _metadataMediator = metadataMediator;
        _viewModelFactory = viewModelFactory;
        _ruleRepository = ruleRepository;
        _assignmentRuleRepository = assignmentRuleRepository;
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
            // OnSelectedNodeChanged fires on the UI thread (PropertyChanged setter),
            // so Dispatcher.CurrentDispatcher returns the UI thread dispatcher.
            var dispatcher = System.Windows.Application.Current?.Dispatcher
                ?? Dispatcher.CurrentDispatcher;
            if (!dispatcher.HasShutdownStarted)
            {
                _ = dispatcher.InvokeAsync(() => LoadAttributesForCategoryAsync(value));
            }
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
        using var _scope = SmartConLogger.BeginScope("CategoryTree",
            ("Method", "LoadTreeAsync"));
        IReadOnlyList<CategoryNode> nodes = [];
        try
        {
            nodes = await _categoryRepository.GetAllAsync(ct);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"CategoryTreeEditor GetAllAsync failed: {ex.Message} [Action: закройте и откройте editor; проверьте БД каталога]");
        }

        IReadOnlyDictionary<string, int> familyCounts = new Dictionary<string, int>();
        try
        {
            familyCounts = await _categoryRepository.GetAllFamilyCountsAsync(ct);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"CategoryTreeEditor GetAllFamilyCountsAsync failed: {ex.Message} [Action: проверьте БД каталога; counts могут быть неполными до Refresh]");
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

            // Full-immediate editor: everything reads straight from the DB.
            var effectiveAttrs = await _bindingService.GetEffectiveAttributesAsync(categoryId);
            var directBindings = await _bindingService.GetDirectBindingsAsync(categoryId);

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

                items.Add(new AttributeListItemViewModel
                {
                    AttributeId = def.Id,
                    Name = def.Name,
                    Group = def.Group,
                    IsBound = effective is not null,
                    IsInherited = effective?.IsInherited ?? false,
                    SourceCategoryName = sourceName,
                    // Direct binding first; for inherited attributes the
                    // effective row carries the PARENT's binding id —
                    // rules edit targets that parent binding.
                    BindingId = binding?.Id ?? effective?.BindingId,
                    IsEnabled = effective?.IsEnabled ?? true,
                    Parent = this
                });
            }

            _allAttributeItems = items;
            await LoadRuleCountsAsync(items);

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
            SmartConLogger.Warn($"LoadAttributesForCategoryAsync failed: {ex.Message} [Action: закройте и откройте editor; проверьте БД каталога]");
        }
    }

    /// <summary>
    /// Import Validation Gate: loads per-binding rule counts for the
    /// shield badges. Failures degrade to no badges (badges are
    /// informational — the editor must still open).
    /// </summary>
    private async Task LoadRuleCountsAsync(List<AttributeListItemViewModel> items)
    {
        try
        {
            var bindingIds = items
                .Where(i => i.BindingId is not null)
                .Select(i => i.BindingId!)
                .ToList();
            if (bindingIds.Count == 0) return;

            var counts = await _ruleRepository.GetRuleCountsForBindingsAsync(bindingIds);
            foreach (var item in items)
            {
                if (item.BindingId is not null && counts.TryGetValue(item.BindingId, out var count))
                {
                    item.RuleCount = count.Total;
                    item.DisabledRuleCount = count.Disabled;
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"LoadRuleCountsAsync failed: {ex.Message} [Action: значки правил не отображены; проверьте БД каталога]");
        }
    }

    /// <summary>
    /// Import Validation Gate: opens the rules editor for the attribute's
    /// binding (own or inherited-from-parent). Refreshes the badge count
    /// afterwards.
    /// </summary>
    internal async Task OpenValidationRulesEditorAsync(AttributeListItemViewModel item)
    {
        if (item.BindingId is null)
        {
            return;
        }

        try
        {
            var vm = _viewModelFactory.CreateValidationRulesEditorViewModel(
                item.BindingId, item.Name, SelectedCategoryPath);
            await vm.InitializeAsync();
            var saved = _dialogService.ShowValidationRulesEditor(vm);

            if (saved == true)
            {
                // Same semantics as LoadRuleCountsAsync: the badge counts
                // ALL rules of the binding (a disabled rule still exists).
                var rules = await _ruleRepository.GetRulesForBindingAsync(item.BindingId);
                item.RuleCount = rules.Count;
                item.DisabledRuleCount = rules.Count(r => !r.IsEnabled);
                _metadataMediator.RaiseMetadataChanged();
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"OpenValidationRulesEditorAsync failed for '{item.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Binding toggle (full-immediate editor): the binding is created /
    /// deleted in the catalog database right away (same atomic semantics
    /// as the metadata import) — the rules shield activates at once and
    /// there is no "save and reopen to edit rules" gap.
    /// </summary>
    internal async Task HandleBindingToggleAsync(AttributeListItemViewModel item, bool shouldBeBound)
    {
        if (SelectedNode is null) return;

        // Inherited bindings belong to the PARENT category — never let an
        // unbind here delete another category's binding (with its rules
        // and every descendant's effective attribute). The checkbox is
        // disabled in XAML for inherited+bound; this is the cheap guard
        // in case that trigger is ever relaxed.
        if (item.IsInherited) return;

        // Import Validation Gate: unbinding deletes the binding's
        // validation rules (FK CASCADE). Warn before destroying them.
        if (!shouldBeBound && item.HasRules)
        {
            var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_UnbindRulesTitle)
                ?? "Правила валидации будут удалены";
            var message = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                LanguageManager.GetString(StringLocalization.Keys.FM_CTE_UnbindRulesMessage)
                    ?? "У атрибута \"{0}\" заданы правила валидации ({1}). При отвязке они будут удалены без возможности восстановления. Продолжить?",
                item.Name,
                item.RuleCount);
            if (!_dialogService.ShowConfirmation(title, message))
            {
                item.NotifyBoundStateChanged();
                return;
            }
        }

        var categoryId = SelectedNode.CategoryId;
        var attributeId = item.AttributeId;

        try
        {
            if (shouldBeBound)
            {
                var sortOrder = (await _bindingService.GetDirectBindingsAsync(categoryId)).Count;
                var created = await _bindingService.CreateBindingAsync(categoryId, attributeId, sortOrder);
                item.BindingId = created.Id;
                item.IsBound = true;
                item.RuleCount = 0;
                item.DisabledRuleCount = 0;
            }
            else
            {
                if (item.BindingId is not null)
                {
                    await _bindingService.DeleteBindingAsync(item.BindingId);
                }

                item.BindingId = null;
                item.IsBound = false;
                item.RuleCount = 0;
                item.DisabledRuleCount = 0;
            }

            _metadataMediator.RaiseMetadataChanged();
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Binding toggle failed for '{item.Name}' in '{SelectedNode.FullPath}': {ex.Message} " +
                "[Action: проверьте БД каталога и повторите]");
            item.NotifyBoundStateChanged();
            return;
        }

        ApplyAttributeFilter();
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
        libraryVm.RequestClose += _ => libraryVm.Detach();
        await libraryVm.InitializeAsync();
        _dialogService.ShowAttributeLibrary(libraryVm);

        if (SelectedNode is not null)
            await LoadAttributesForCategoryAsync(SelectedNode);
    }

    /// <summary>
    /// Atomic metadata import: writes package bindings + their validation
    /// rules directly to the catalog database (dedupe: existing binding
    /// keeps its own rules — the package never silently overwrites them).
    /// </summary>
    private async Task<BindingImportResult> ImportBindingsToDbAsync(List<MetadataExportBinding> bindings)
    {
        var bindingsImported = 0;
        var bindingsSkipped = 0;
        var rulesImported = 0;
        var rulesSkipped = 0;
        var warnings = new List<string>();

        var allCategories = await _categoryRepository.GetAllAsync();
        var pathToCategory = allCategories
            .ToDictionary(c => c.FullPath, c => c, StringComparer.OrdinalIgnoreCase);

        var allAttributes = await _attributeDefRepository.GetAllAsync();
        var nameToAttr = allAttributes
            .ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);

        foreach (var binding in bindings)
        {
            if (!pathToCategory.TryGetValue(binding.CategoryPath, out var category))
            {
                warnings.Add($"Binding skipped: category '{binding.CategoryPath}' not found.");
                bindingsSkipped++;
                continue;
            }

            if (!nameToAttr.TryGetValue(binding.AttributeName, out var attribute))
            {
                warnings.Add($"Binding skipped: attribute '{binding.AttributeName}' not found.");
                bindingsSkipped++;
                continue;
            }

            var existingBindings = await _bindingService.GetDirectBindingsAsync(category.Id);
            if (existingBindings.Any(b => b.AttributeId == attribute.Id))
            {
                // Existing binding keeps its own rules — the package does
                // not silently overwrite them.
                bindingsSkipped++;
                rulesSkipped += binding.ValidationRules.Count;
                continue;
            }

            var created = await _bindingService.CreateBindingAsync(category.Id, attribute.Id, binding.SortOrder);
            bindingsImported++;

            foreach (var exportedRule in binding.ValidationRules)
            {
                if (!Enum.TryParse<ValidationRuleOperator>(exportedRule.Operator, out var ruleOperator)
                    || !Enum.IsDefined(typeof(ValidationRuleOperator), ruleOperator))
                {
                    warnings.Add($"Rule skipped: unknown operator '{exportedRule.Operator}' for '{binding.AttributeName}'.");
                    rulesSkipped++;
                    continue;
                }

                await _ruleRepository.CreateRuleAsync(new ValidationRule(
                    string.Empty,
                    created.Id,
                    ruleOperator,
                    exportedRule.ValueText,
                    exportedRule.ValueNumber,
                    exportedRule.MinValue,
                    exportedRule.MaxValue,
                    UnitTypeId: null,
                    SortOrder: 0,
                    exportedRule.IsEnabled));
                rulesImported++;
            }
        }

        return new BindingImportResult(bindingsImported, bindingsSkipped, rulesImported, rulesSkipped, warnings);
    }

    private sealed record BindingImportResult(
        int BindingsImported,
        int BindingsSkipped,
        int RulesImported,
        int RulesSkipped,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// Full-immediate editor: every change is committed the moment it is
    /// made, so closing never discards anything — no confirm needed.
    /// </summary>
    public void ConfirmClose(CloseConfirmationArgs args)
    {
        args.DialogResult = true;
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke(true);
    }
}


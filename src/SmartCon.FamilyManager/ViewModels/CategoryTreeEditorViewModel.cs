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

        // #241: per-category assignment rule counts for the tree filter icon.
        var (assignmentCounts, assignmentDisabledCounts) = await LoadAssignmentRuleCountsAsync(ct);

        var tree = new CategoryTree(nodes);
        RootNodes = BuildTreeNodes(tree, null, familyCounts);
        ApplyAssignmentRuleCounts(RootNodes, assignmentCounts, assignmentDisabledCounts);
    }

    /// <summary>#241: loads (total, disabled) assignment group counts per
    /// category — the tree icon's three states. Failure degrades to empty
    /// counts (gray icons), never breaks the tree load.</summary>
    private async Task<(IReadOnlyDictionary<string, int> Total, IReadOnlyDictionary<string, int> Disabled)> LoadAssignmentRuleCountsAsync(CancellationToken ct)
    {
        try
        {
            var groups = await _assignmentRuleRepository.GetGroupsWithConditionsAsync(ct);
            var total = new Dictionary<string, int>();
            var disabled = new Dictionary<string, int>();
            foreach (var group in groups)
            {
                total[group.CategoryId] = total.TryGetValue(group.CategoryId, out var count) ? count + 1 : 1;
                if (!group.IsEnabled)
                {
                    disabled[group.CategoryId] = disabled.TryGetValue(group.CategoryId, out var disabledCount) ? disabledCount + 1 : 1;
                }
            }

            return (total, disabled);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"CategoryTreeEditor assignment rule counts failed: {ex.Message} [Action: проверьте БД каталога; индикаторы правил могут быть скрыты до обновления]");
            return (new Dictionary<string, int>(), new Dictionary<string, int>());
        }
    }

    private static void ApplyAssignmentRuleCounts(
        IEnumerable<CatalogTreeNodeViewModel> nodes,
        IReadOnlyDictionary<string, int> total,
        IReadOnlyDictionary<string, int> disabled)
    {
        foreach (var node in nodes)
        {
            if (node is CategoryNodeViewModel cat)
            {
                cat.AssignmentRuleCount = total.TryGetValue(cat.CategoryId, out var count) ? count : 0;
                cat.DisabledAssignmentRuleCount = disabled.TryGetValue(cat.CategoryId, out var disabledCount) ? disabledCount : 0;
            }
            ApplyAssignmentRuleCounts(node.Children, total, disabled);
        }
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


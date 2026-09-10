using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SKeys = SmartCon.UI.StringLocalization.Keys;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Диалог расширенного поиска (#87): охват по категории (поддерево каталога,
/// выбирается классическим пикером-деревом) + условия по атрибутам
/// библиотеки и системным полям (словарь #241), объединённые по «И». Список
/// атрибутов зависит от выбранной категории (effective-привязки, как на
/// вкладке «Атрибуты» свойств семейства). Диалог ничего не применяет сам —
/// по Apply закрывается с результатом true, а
/// <see cref="FamilyManagerMainViewModel"/> забирает снимок через
/// <see cref="BuildFilter"/>.
/// </summary>
public sealed partial class AdvancedSearchViewModel : ObservableObject, IObservableRequestClose
{
    public event Action<bool?>? RequestClose;

    private readonly ICategoryRepository _categoryRepository;
    private readonly ICategoryAttributeBindingService _bindingService;
    private readonly IAttributeDefinitionRepository _attributeDefRepository;
    private readonly IAttributeValueRepository _valueRepository;
    private readonly IRevitCategoryLabelService _revitCategoryLabels;
    private readonly IFamilyManagerDialogService _dialogService;

    private readonly Dictionary<string, string> _categoryPaths = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<AdvancedSearchOperatorItem> _attributeOperators;
    private readonly IReadOnlyList<AdvancedSearchOperatorItem> _systemTextOperators;
    private readonly IReadOnlyList<AdvancedSearchOperatorItem> _systemOrdinalOperators;

    public sealed record AdvancedSearchOperatorItem(ValidationRuleOperator Operator, string DisplayName);

    /// <summary>Системные поля, доступные в поиске (SystemFamilyKey не
    /// редактируется — как в редакторе автоназначения).</summary>
    public IReadOnlyList<AssignmentSystemFieldItem> SystemFields { get; }

    public IReadOnlyList<AssignmentValueItem> RevitCategories { get; private set; } = [];

    public IReadOnlyList<AssignmentValueItem> PartTypes { get; } = [];

    /// <summary>Атрибуты выбранной категории (или все активные при «Все категории»).</summary>
    public IReadOnlyList<EffectiveCategoryAttribute> AvailableAttributes { get; private set; } = [];

    [ObservableProperty]
    private string? _selectedCategoryId;

    [ObservableProperty]
    private ObservableCollection<AdvancedSearchConditionViewModel> _conditions = [];

    public bool HasConditions => Conditions.Count > 0;

    public bool HasNoConditions => Conditions.Count == 0;

    public bool CanApply => Conditions.All(c => c.IsValid);

    /// <summary>Отображение выбранной категории: полный путь из дерева
    /// каталога (или «Все категории»).</summary>
    public string SelectedCategoryPath =>
        SelectedCategoryId is null
            ? Localize(SKeys.FM_AdvSearch_AllCategories, "Все категории")
            : _categoryPaths.TryGetValue(SelectedCategoryId, out var path)
                ? path
                : SelectedCategoryId;

    public AdvancedSearchViewModel(
        ICategoryRepository categoryRepository,
        ICategoryAttributeBindingService bindingService,
        IAttributeDefinitionRepository attributeDefRepository,
        IAttributeValueRepository valueRepository,
        IRevitCategoryLabelService revitCategoryLabels,
        IFamilyManagerDialogService dialogService)
    {
        _categoryRepository = categoryRepository;
        _bindingService = bindingService;
        _attributeDefRepository = attributeDefRepository;
        _valueRepository = valueRepository;
        _revitCategoryLabels = revitCategoryLabels;
        _dialogService = dialogService;

        _attributeOperators = BuildOperators(AttributeFilterCondition.AttributeOperators);
        _systemTextOperators = BuildOperators(AttributeFilterCondition.SystemTextOperators);
        _systemOrdinalOperators = BuildOperators(AttributeFilterCondition.SystemOrdinalOperators);

        SystemFields =
        [
            new(AssignmentSystemField.RevitCategory, Localize(SKeys.FM_AssignEditor_Field_RevitCategory, "Категория Revit")),
            new(AssignmentSystemField.PartType, Localize(SKeys.FM_AssignEditor_Field_PartType, "Тип детали")),
            new(AssignmentSystemField.FamilyName, Localize(SKeys.FM_AssignEditor_Field_FamilyName, "Имя семейства")),
        ];

        PartTypes = PartTypeLabelMap.GetAllEntries()
            .Select(e => new AssignmentValueItem(e.Key, e.Label))
            .ToList();
    }

    /// <summary>
    /// Загружает категории и восстанавливает текущее состояние фильтра,
    /// чтобы повторное открытие диалога показывало активные условия.
    /// </summary>
    public async Task InitializeAsync(AdvancedSearchFilter? current, CancellationToken ct = default)
    {
        try
        {
            var categories = await _categoryRepository.GetAllAsync(ct);
            _categoryPaths.Clear();
            foreach (var category in categories)
            {
                _categoryPaths[category.Id] = category.FullPath;
            }

            RevitCategories = _revitCategoryLabels.GetModelCategories()
                .Select(c => new AssignmentValueItem(
                    c.Ordinal.ToString(CultureInfo.InvariantCulture), c.Label))
                .ToList();
            OnPropertyChanged(nameof(RevitCategories));
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"AdvancedSearch: категории не загрузились: {ex.Message} [Action: переоткройте диалог или проверьте БД каталога]");
        }

        SelectedCategoryId = current?.CategoryId;

        // Сначала атрибуты выбранной категории, потом условия — иначе
        // восстановленные строки проваливают IsValid (атрибута нет в списке).
        await LoadAttributesForCurrentCategoryAsync();

        if (current is not null)
        {
            foreach (var condition in current.Conditions)
            {
                var row = new AdvancedSearchConditionViewModel(this);
                Conditions.Add(row);
                row.Restore(
                    condition.SourceKind,
                    condition.AttributeId,
                    condition.AttributeName,
                    condition.SystemField,
                    condition.Operator,
                    condition.Value);
            }
        }
    }

    public AdvancedSearchFilter BuildFilter()
    {
        var conditions = Conditions
            .Where(c => c.IsValid)
            .Select(c => new AttributeFilterCondition(
                c.SourceKind,
                c.IsAttributeSource ? c.SelectedAttributeId : null,
                c.IsAttributeSource ? (c.SelectedAttributeName ?? string.Empty) : null,
                c.IsAttributeSource ? null : c.SystemField,
                c.Operator,
                c.ValueText?.Trim()))
            .ToList();

        return new AdvancedSearchFilter(SelectedCategoryId, conditions);
    }

    internal async Task<IReadOnlyList<string>> GetSuggestionsAsync(string attributeId, string attributeName)
        => await _valueRepository.GetDistinctValueTextsAsync(attributeId, attributeName);

    internal IReadOnlyList<AdvancedSearchOperatorItem> GetOperatorsFor(
        AssignmentConditionSourceKind sourceKind, AssignmentSystemField? systemField) =>
        sourceKind == AssignmentConditionSourceKind.Attribute
            ? _attributeOperators
            : systemField is AssignmentSystemField.FamilyName or AssignmentSystemField.SystemFamilyKey
                ? _systemTextOperators
                : _systemOrdinalOperators;

    /// <summary>Пересчёт CanApply при изменении любой строки-условия.</summary>
    internal void NotifyConditionChanged()
    {
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    partial void OnConditionsChanged(ObservableCollection<AdvancedSearchConditionViewModel> value)
    {
        value.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasConditions));
            OnPropertyChanged(nameof(HasNoConditions));
            NotifyConditionChanged();
        };
        OnPropertyChanged(nameof(HasConditions));
        OnPropertyChanged(nameof(HasNoConditions));
        NotifyConditionChanged();
    }

    partial void OnSelectedCategoryIdChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedCategoryPath));
        _ = LoadAttributesForCurrentCategoryAsync();
    }

    /// <summary>
    /// Классический пикер-дерево категорий каталога: плоский список в
    /// комбобоксе не масштабируется на большие каталоги (#87, итерация 2).
    /// ShowCategoryPicker: null — отмена, "" — очистить («Все категории»),
    /// иначе — выбранный Id.
    /// </summary>
    [RelayCommand]
    private async Task PickCategoryAsync()
    {
        var picker = new CategoryPickerViewModel(_categoryRepository, allowClear: true);
        await picker.InitializeAsync();
        var result = _dialogService.ShowCategoryPicker(picker);
        if (result is null)
        {
            return;
        }

        SelectedCategoryId = result.Length == 0 ? null : result;
    }

    /// <summary>
    /// Перезагружает список атрибутов под выбранную категорию и пересчитывает
    /// валидность строк («чужой» атрибут после смены категории блокирует
    /// Apply). internal — для await в тестах (событие смены категории
    /// запускает тот же метод fire-and-forget).
    /// </summary>
    internal async Task LoadAttributesForCurrentCategoryAsync()
    {
        await LoadAttributesAsync();
        foreach (var condition in Conditions)
        {
            condition.OnAvailableAttributesChanged();
        }
        NotifyConditionChanged();
    }

    private async Task LoadAttributesAsync(CancellationToken ct = default)
    {
        try
        {
            var defs = await _attributeDefRepository.GetAllAsync(ct);
            var activeIds = defs.Where(a => a.IsActive).Select(a => a.Id).ToHashSet();

            if (SelectedCategoryId is { } categoryId)
            {
                // Тот же фильтр, что на вкладке «Атрибуты» свойств семейства:
                // effective-привязки (включая унаследованные) + активные дефиниции.
                var effective = await _bindingService.GetEffectiveAttributesAsync(categoryId, ct);
                AvailableAttributes = effective
                    .Where(a => a.IsEnabled && activeIds.Contains(a.AttributeId))
                    .OrderBy(a => a.Name, StringComparer.CurrentCulture)
                    .ToList();
            }
            else
            {
                AvailableAttributes = defs
                    .Where(a => a.IsActive)
                    .Select(a => new EffectiveCategoryAttribute(a.Id, a.Name, a.Group, 0, true, false, null, null))
                    .OrderBy(a => a.Name, StringComparer.CurrentCulture)
                    .ToList();
            }

            OnPropertyChanged(nameof(AvailableAttributes));
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"AdvancedSearch: атрибуты не загрузились: {ex.Message} [Action: переоткройте диалог или проверьте БД каталога]");
            AvailableAttributes = [];
            OnPropertyChanged(nameof(AvailableAttributes));
        }
    }

    [RelayCommand]
    private void AddCondition()
    {
        Conditions.Add(new AdvancedSearchConditionViewModel(this));
    }

    [RelayCommand]
    private void DeleteCondition(AdvancedSearchConditionViewModel? condition)
    {
        if (condition is not null)
        {
            condition.IsSuggestionsOpen = false;
            Conditions.Remove(condition);
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        foreach (var condition in Conditions)
        {
            condition.IsSuggestionsOpen = false;
        }

        RequestClose?.Invoke(true);
    }

    /// <summary>
    /// Сброс: очищает условия и категорию и закрывается с true — главный VM
    /// применит пустой фильтр (дерево перезагрузится без сужения).
    /// </summary>
    [RelayCommand]
    private void Reset()
    {
        Conditions.Clear();
        SelectedCategoryId = null;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }

    private IReadOnlyList<AdvancedSearchOperatorItem> BuildOperators(
        IEnumerable<ValidationRuleOperator> operators) =>
        operators.Select(op => new AdvancedSearchOperatorItem(op, DescribeOperator(op))).ToList();

    private static string DescribeOperator(ValidationRuleOperator op) => op switch
    {
        ValidationRuleOperator.Equals => Localize(SKeys.FM_RuleOp_Equals, "Равно"),
        ValidationRuleOperator.NotEquals => Localize(SKeys.FM_RuleOp_NotEquals, "Не равно"),
        ValidationRuleOperator.Contains => Localize(SKeys.FM_RuleOp_Contains, "Содержит"),
        ValidationRuleOperator.NotContains => Localize(SKeys.FM_RuleOp_NotContains, "Не содержит"),
        ValidationRuleOperator.HasValue => Localize(SKeys.FM_SearchOp_HasValue, "Заполнено"),
        ValidationRuleOperator.IsEmpty => Localize(SKeys.FM_SearchOp_IsEmpty, "Не заполнено"),
        _ => op.ToString(),
    };

    private static string Localize(string key, string fallback) =>
        SmartCon.UI.LanguageManager.GetString(key) ?? fallback;
}

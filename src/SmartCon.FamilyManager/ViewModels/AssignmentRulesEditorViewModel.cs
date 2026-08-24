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
/// Editor for the auto-assignment rules of ONE category (#241): OR-groups
/// of AND-conditions. Opened from the category tree editor's context menu
/// ("Правила автоназначения..."). Saves directly to the catalog database
/// via <see cref="IAssignmentRuleRepository"/> (groups live and die with
/// their category — FK CASCADE).
/// </summary>
public sealed partial class AssignmentRulesEditorViewModel : ObservableObject, IObservableRequestClose
{
    public event Action<bool?>? RequestClose;

    private readonly string _categoryId;
    private readonly IAssignmentRuleRepository _ruleRepository;
    private readonly IAttributeDefinitionRepository _attributeRepository;
    private readonly IRevitCategoryLabelService _revitCategoryLabels;
    private readonly string? _copyFromCategoryId;
    private readonly string? _copyFromCategoryPath;

    public string CategoryPath { get; }

    [ObservableProperty]
    private ObservableCollection<AssignmentGroupRowViewModel> _groups = [];

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Nearest rule-bearing ancestor exists — the editor shows the
    /// «Взять условия родителя» button (#241).</summary>
    public bool HasParentRulesToCopy => _copyFromCategoryId is not null;

    /// <summary>Names the ancestor whose rules will be copied.</summary>
    public string CopyParentRulesTooltip =>
        string.Format(
            Localize(
                SKeys.FM_AssignEditor_CopyParentTooltip,
                "Копирует правила категории «{0}» в этот редактор как отправную точку. После копирования правила полностью независимы."),
            _copyFromCategoryPath ?? string.Empty);

    public IReadOnlyList<AssignmentOperatorItem> AttributeOperators { get; }
    public IReadOnlyList<AssignmentOperatorItem> OrdinalOperators { get; }
    public IReadOnlyList<AssignmentOperatorItem> TextOperators { get; }
    public IReadOnlyList<AssignmentSystemFieldItem> SystemFields { get; }
    public IReadOnlyList<AttributeDefinition> AvailableAttributes { get; private set; } = [];
    public IReadOnlyList<AssignmentValueItem> RevitCategories { get; private set; } = [];
    public IReadOnlyList<AssignmentValueItem> PartTypes { get; private set; } = [];

    public AssignmentRulesEditorViewModel(
        string categoryId,
        string categoryPath,
        IAssignmentRuleRepository ruleRepository,
        IAttributeDefinitionRepository attributeRepository,
        IRevitCategoryLabelService revitCategoryLabels,
        string? copyFromCategoryId = null,
        string? copyFromCategoryPath = null)
    {
        _categoryId = categoryId;
        _ruleRepository = ruleRepository;
        _attributeRepository = attributeRepository;
        _revitCategoryLabels = revitCategoryLabels;
        _copyFromCategoryId = copyFromCategoryId;
        _copyFromCategoryPath = copyFromCategoryPath;
        CategoryPath = categoryPath;

        AttributeOperators = AssignmentOperatorPolicy.AttributeOperators
            .Select(op => new AssignmentOperatorItem(op, DescribeOperator(op)))
            .ToList();
        OrdinalOperators = AssignmentOperatorPolicy.SystemOperators(AssignmentSystemField.RevitCategory)
            .Select(op => new AssignmentOperatorItem(op, DescribeOperator(op)))
            .ToList();
        TextOperators = AssignmentOperatorPolicy.SystemOperators(AssignmentSystemField.FamilyName)
            .Select(op => new AssignmentOperatorItem(op, DescribeOperator(op)))
            .ToList();

        SystemFields =
        [
            new(AssignmentSystemField.RevitCategory, Localize(SKeys.FM_AssignEditor_Field_RevitCategory, "Категория Revit")),
            new(AssignmentSystemField.PartType, Localize(SKeys.FM_AssignEditor_Field_PartType, "Тип детали")),
            new(AssignmentSystemField.FamilyName, Localize(SKeys.FM_AssignEditor_Field_FamilyName, "Имя семейства")),
        ];
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var attributes = await _attributeRepository.GetAllAsync(ct);
        AvailableAttributes = attributes
            .Where(a => a.IsActive)
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .ToList();

        RevitCategories = _revitCategoryLabels.GetModelCategories()
            .Select(c => new AssignmentValueItem(
                c.Ordinal.ToString(CultureInfo.InvariantCulture), c.Label))
            .ToList();
        PartTypes = PartTypeLabelMap.GetAllEntries()
            .Select(e => new AssignmentValueItem(e.Key, e.Label))
            .ToList();

        var groups = await _ruleRepository.GetGroupsForCategoryAsync(_categoryId, ct);
        Groups = new ObservableCollection<AssignmentGroupRowViewModel>(
            groups.OrderBy(g => g.SortOrder)
                .Select((g, index) => new AssignmentGroupRowViewModel(
                    g.Id,
                    index + 1,
                    g.IsEnabled,
                    g.Conditions.OrderBy(c => c.SortOrder)
                        .Where(ConditionEditable)
                        .Select(c => CreateConditionRow(c, c.Id)))));
    }

    /// <summary>#241: appends the nearest rule-bearing ancestor's groups to
    /// this editor as NEW rows (ids null — saved as fresh rules). Pure
    /// editor sugar: the copies are fully independent afterwards, nothing
    /// links them to the parent at runtime.</summary>
    [RelayCommand]
    private async Task CopyParentRulesAsync()
    {
        if (_copyFromCategoryId is null)
        {
            return;
        }

        using var _scope = SmartConLogger.BeginScope("AssignRules",
            ("Method", nameof(CopyParentRulesAsync)),
            ("CopyFromCategoryId", _copyFromCategoryId));

        try
        {
            var groups = await _ruleRepository.GetGroupsForCategoryAsync(_copyFromCategoryId);
            var added = 0;
            foreach (var group in groups.OrderBy(g => g.SortOrder))
            {
                var conditions = group.Conditions.OrderBy(c => c.SortOrder)
                    .Where(ConditionEditable)
                    .Select(c => CreateConditionRow(c, null))
                    .ToList();
                if (conditions.Count == 0)
                {
                    continue;
                }

                Groups.Add(new AssignmentGroupRowViewModel(null, Groups.Count + 1, group.IsEnabled, conditions));
                added++;
            }

            if (added == 0)
            {
                StatusMessage = Localize(
                    SKeys.FM_AssignEditor_CopyParentEmpty,
                    "В правилах родителя нет условий для копирования");
                return;
            }

            StatusMessage = string.Format(
                Localize(
                    SKeys.FM_AssignEditor_CopyParentDone,
                    "Добавлено групп из «{0}»: {1}. Проверьте условия и сохраните."),
                _copyFromCategoryPath ?? string.Empty,
                added);
            SmartConLogger.Info($"Copied {added} assignment rule group(s) from '{_copyFromCategoryPath}'");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Copy parent rules failed: {ex.Message} [Action: проверьте БД каталога и лог]");
            StatusMessage = string.Format(
                SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_ImportError) ?? "Error: {0}",
                ex.Message);
        }
    }

    private AssignmentConditionRowViewModel CreateConditionRow(AssignmentCondition c, string? id) =>
        new(
            id, c.SourceKind, c.AttributeId, c.SystemField, c.Operator,
            c.ValueText, c.ValueNumber, c.MinValue, c.MaxValue, c.IsEnabled,
            AttributeOperators, OrdinalOperators, TextOperators,
            RevitCategories, PartTypes);

    /// <summary>
    /// SystemFamilyKey conditions are engine-supported but not editable in
    /// the dialog (no user-friendly picker). A stored one (e.g. from a
    /// hand-edited test database) is dropped on save — with a warning, not
    /// a silent binding-null CHECK violation.
    /// </summary>
    private static bool ConditionEditable(AssignmentCondition condition)
    {
        if (condition.SystemField == AssignmentSystemField.SystemFamilyKey)
        {
            SmartConLogger.Warn(
                $"Assignment editor: condition {condition.Id} targets SystemFamilyKey which is not editable — it is skipped (load/copy) and will not be saved " +
                "[Action: если условие ещё нужно — пересоздайте его по поддерживаемому полю (Категория Revit / Тип детали / Имя семейства)]");
            return false;
        }

        return true;
    }

    [RelayCommand]
    private void AddGroup()
    {
        Groups.Add(new AssignmentGroupRowViewModel(
            null, Groups.Count + 1, true,
            [CreateNewCondition()]));
    }

    [RelayCommand]
    private void DeleteGroup(AssignmentGroupRowViewModel? group)
    {
        if (group is null) return;
        Groups.Remove(group);
        RenumberGroups();
    }

    [RelayCommand]
    private void AddCondition(AssignmentGroupRowViewModel? group)
    {
        group?.Conditions.Add(CreateNewCondition());
    }

    [RelayCommand]
    private void DeleteCondition(AssignmentConditionRowViewModel? condition)
    {
        if (condition is null) return;
        var group = Groups.FirstOrDefault(g => g.Conditions.Contains(condition));
        group?.Conditions.Remove(condition);
    }

    private AssignmentConditionRowViewModel CreateNewCondition() =>
        new(null, AssignmentConditionSourceKind.Attribute, null, null,
            ValidationRuleOperator.Contains, null, null, null, null, true,
            AttributeOperators, OrdinalOperators, TextOperators,
            RevitCategories, PartTypes);

    private void RenumberGroups()
    {
        for (var i = 0; i < Groups.Count; i++)
        {
            Groups[i].Number = i + 1;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        using var _scope = SmartConLogger.BeginScope("AssignRules",
            ("Method", nameof(SaveAsync)),
            ("CategoryId", _categoryId));

        if (!Validate(out var error))
        {
            StatusMessage = error!;
            return;
        }

        try
        {
            var existing = await _ruleRepository.GetGroupsForCategoryAsync(_categoryId);
            var presentIds = Groups.Where(g => g.Id is not null).Select(g => g.Id!).ToHashSet();
            foreach (var removed in existing.Where(g => !presentIds.Contains(g.Id)))
            {
                await _ruleRepository.DeleteGroupAsync(removed.Id);
            }

            var sortOrder = 0;
            foreach (var groupRow in Groups)
            {
                // A group with zero conditions is dropped silently — it can
                // never match anything and only clutters the tree icon.
                if (groupRow.Conditions.Count == 0)
                {
                    if (groupRow.Id is not null)
                    {
                        await _ruleRepository.DeleteGroupAsync(groupRow.Id);
                    }
                    continue;
                }

                AssignmentRuleGroup group;
                if (groupRow.Id is null)
                {
                    group = await _ruleRepository.CreateGroupAsync(_categoryId);
                    // Write the id back immediately: if a later condition
                    // insert fails, the retry must UPDATE this group, not
                    // create an orphan duplicate (incident of beta.9).
                    groupRow.SetPersistedId(group.Id);
                }
                else
                {
                    group = existing.First(g => g.Id == groupRow.Id);
                    await _ruleRepository.UpdateGroupAsync(group.Id, sortOrder, groupRow.IsEnabled);
                }

                var existingConditions = group.Conditions.ToDictionary(c => c.Id);
                var presentConditionIds = groupRow.Conditions
                    .Where(c => c.Id is not null)
                    .Select(c => c.Id!)
                    .ToHashSet();
                foreach (var removedCondition in existingConditions.Values.Where(c => !presentConditionIds.Contains(c.Id)))
                {
                    await _ruleRepository.DeleteConditionAsync(removedCondition.Id);
                }

                var conditionSortOrder = 0;
                foreach (var conditionRow in groupRow.Conditions)
                {
                    var condition = MapCondition(group.Id, conditionRow, conditionSortOrder++);
                    if (conditionRow.Id is null)
                    {
                        var created = await _ruleRepository.CreateConditionAsync(
                            group.Id, condition.SourceKind, condition.AttributeId, condition.SystemField,
                            condition.Operator, condition.ValueText, condition.ValueNumber,
                            condition.MinValue, condition.MaxValue, condition.IsEnabled);
                        conditionRow.SetPersistedId(created.Id);
                    }
                    else
                    {
                        await _ruleRepository.UpdateConditionAsync(condition);
                    }
                }

                sortOrder++;
            }

            var savedGroupCount = Groups.Count(g => g.Conditions.Count > 0);
            SmartConLogger.Info($"Saved {savedGroupCount} assignment rule group(s) for category {_categoryId}");
            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Failed to save assignment rules: {ex.Message}");
            StatusMessage = string.Format(
                SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_ImportError) ?? "Error: {0}",
                ex.Message);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }

    private bool Validate(out string? error)
    {
        // Zero groups is a VALID final state — the user may delete all
        // rules and save: the category returns to "no rules" (gray icon).
        foreach (var group in Groups)
        {
            foreach (var row in group.Conditions)
            {
                if (row.IsAttributeSource && string.IsNullOrEmpty(row.SelectedAttributeId))
                {
                    error = Localize(SKeys.FM_AssignEditor_ErrorAttributeRequired, "Выберите атрибут");
                    return false;
                }

                if (!row.IsAttributeSource && row.SystemField is null)
                {
                    error = Localize(SKeys.FM_AssignEditor_ErrorFieldRequired, "Выберите системное поле");
                    return false;
                }

                if (row.ShowCategoryPicker && string.IsNullOrEmpty(row.ValueText))
                {
                    error = Localize(SKeys.FM_AssignEditor_ErrorCategoryRequired, "Выберите категорию Revit");
                    return false;
                }

                if (row.ShowPartTypePicker && string.IsNullOrEmpty(row.ValueText))
                {
                    error = Localize(SKeys.FM_AssignEditor_ErrorPartTypeRequired, "Выберите тип детали");
                    return false;
                }

                if (row.ShowValueField && string.IsNullOrWhiteSpace(row.ValueText))
                {
                    error = Localize(SKeys.FM_RulesEditor_ErrorValueRequired, "Для этого оператора нужно значение");
                    return false;
                }

                if (row.ShowNumberField && ParseNumber(row.ValueNumberText) is null)
                {
                    error = Localize(SKeys.FM_RulesEditor_ErrorNumberRequired, "Для этого оператора нужно число");
                    return false;
                }

                if (row.ShowRangeFields)
                {
                    var min = ParseNumber(row.MinValueText);
                    var max = ParseNumber(row.MaxValueText);
                    if (min is null || max is null)
                    {
                        error = Localize(SKeys.FM_RulesEditor_ErrorRangeRequired, "Обе границы диапазона должны быть числами");
                        return false;
                    }

                    if (min.Value > max.Value)
                    {
                        error = Localize(SKeys.FM_RulesEditor_ErrorRangeOrder, "Нижняя граница не должна превышать верхнюю");
                        return false;
                    }
                }
            }
        }

        error = null;
        return true;
    }

    private AssignmentCondition MapCondition(string groupId, AssignmentConditionRowViewModel row, int sortOrder)
    {
        string? valueText = null;
        double? valueNumber = null;
        double? minValue = null;
        double? maxValue = null;

        if (row.ShowCategoryPicker || row.ShowPartTypePicker)
        {
            valueText = row.ValueText;
        }
        else if (row.ShowValueField)
        {
            // Equals/NotEquals auto-detect number vs text for attribute
            // conditions; system text fields stay text.
            var parsedNumber = row.SourceKind == AssignmentConditionSourceKind.Attribute
                && row.Operator is ValidationRuleOperator.Equals or ValidationRuleOperator.NotEquals
                    ? ParseNumber(row.ValueText)
                    : null;

            if (parsedNumber is not null)
            {
                valueNumber = parsedNumber;
            }
            else
            {
                valueText = row.ValueText;
            }
        }
        else if (row.ShowNumberField)
        {
            valueNumber = ParseNumber(row.ValueNumberText);
        }
        else if (row.ShowRangeFields)
        {
            minValue = ParseNumber(row.MinValueText);
            maxValue = ParseNumber(row.MaxValueText);
        }

        return new AssignmentCondition(
            row.Id ?? string.Empty,
            groupId,
            row.SourceKind,
            row.SelectedAttributeId,
            row.SystemField,
            row.Operator,
            valueText,
            valueNumber,
            minValue,
            maxValue,
            sortOrder,
            row.IsEnabled);
    }

    private static double? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant))
            return invariant;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var current))
            return current;
        return null;
    }

    private static string Localize(string key, string fallback) =>
        SmartCon.UI.LanguageManager.GetString(key) ?? fallback;

    private static string DescribeOperator(ValidationRuleOperator op) =>
        op switch
        {
            ValidationRuleOperator.IsPresent => Localize(SKeys.FM_RuleOp_IsPresent, "Параметр существует"),
            ValidationRuleOperator.HasValue => Localize(SKeys.FM_RuleOp_HasValue, "Есть значение"),
            ValidationRuleOperator.IsEmpty => Localize(SKeys.FM_RuleOp_IsEmpty, "Пусто"),
            ValidationRuleOperator.Equals => Localize(SKeys.FM_RuleOp_Equals, "Равно"),
            ValidationRuleOperator.NotEquals => Localize(SKeys.FM_RuleOp_NotEquals, "Не равно"),
            ValidationRuleOperator.Contains => Localize(SKeys.FM_RuleOp_Contains, "Содержит"),
            ValidationRuleOperator.NotContains => Localize(SKeys.FM_RuleOp_NotContains, "Не содержит"),
            ValidationRuleOperator.GreaterThan => ">",
            ValidationRuleOperator.GreaterOrEqual => "≥",
            ValidationRuleOperator.LessThan => "<",
            ValidationRuleOperator.LessOrEqual => "≤",
            ValidationRuleOperator.Between => Localize(SKeys.FM_RuleOp_Between, "Между"),
            _ => op.ToString(),
        };
}

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

    public string CategoryPath { get; }

    [ObservableProperty]
    private ObservableCollection<AssignmentGroupRowViewModel> _groups = [];

    [ObservableProperty]
    private string _statusMessage = string.Empty;

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
        IRevitCategoryLabelService revitCategoryLabels)
    {
        _categoryId = categoryId;
        _ruleRepository = ruleRepository;
        _attributeRepository = attributeRepository;
        _revitCategoryLabels = revitCategoryLabels;
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
            new(AssignmentSystemField.SystemFamilyKey, Localize(SKeys.FM_AssignEditor_Field_SystemFamilyKey, "Ключ системного семейства")),
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
                        .Select(c => new AssignmentConditionRowViewModel(
                            c.Id, c.SourceKind, c.AttributeId, c.SystemField, c.Operator,
                            c.ValueText, c.ValueNumber, c.MinValue, c.MaxValue, c.IsEnabled,
                            AttributeOperators, OrdinalOperators, TextOperators)))));
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
            AttributeOperators, OrdinalOperators, TextOperators);

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
                AssignmentRuleGroup group;
                if (groupRow.Id is null)
                {
                    group = await _ruleRepository.CreateGroupAsync(_categoryId);
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
                        await _ruleRepository.CreateConditionAsync(
                            group.Id, condition.SourceKind, condition.AttributeId, condition.SystemField,
                            condition.Operator, condition.ValueText, condition.ValueNumber,
                            condition.MinValue, condition.MaxValue, condition.IsEnabled);
                    }
                    else
                    {
                        await _ruleRepository.UpdateConditionAsync(condition);
                    }
                }

                sortOrder++;
            }

            SmartConLogger.Info($"Saved {Groups.Count} assignment rule group(s) for category {_categoryId}");
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
        if (Groups.Count == 0)
        {
            error = Localize(SKeys.FM_AssignEditor_ErrorNoGroups, "Добавьте хотя бы одну группу с условием");
            return false;
        }

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
            // Equals auto-detects number vs text for attribute conditions;
            // system text fields stay text.
            if (row.SourceKind == AssignmentConditionSourceKind.Attribute
                && row.Operator is ValidationRuleOperator.Equals or ValidationRuleOperator.NotEquals
                && ParseNumber(row.ValueText) is not null)
            {
                valueNumber = ParseNumber(row.ValueText);
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

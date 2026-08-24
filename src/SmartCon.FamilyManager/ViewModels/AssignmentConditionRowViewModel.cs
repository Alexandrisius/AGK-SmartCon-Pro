using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// One editable OR-group of the auto-assignment editor (#241): a header
/// (number, enable toggle) plus the AND-conditions collection. Persisted
/// shape is <see cref="AssignmentRuleGroup"/>.
/// </summary>
public sealed partial class AssignmentGroupRowViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private int _number;

    [ObservableProperty]
    private ObservableCollection<AssignmentConditionRowViewModel> _conditions;

    public string? Id { get; }

    public AssignmentGroupRowViewModel(string? id, int number, bool isEnabled, IEnumerable<AssignmentConditionRowViewModel> conditions)
    {
        Id = id;
        _number = number;
        _isEnabled = isEnabled;
        _conditions = new ObservableCollection<AssignmentConditionRowViewModel>(conditions);
    }
}

/// <summary>
/// One editable condition row of the auto-assignment editor (#241).
/// Source switching (attribute ↔ system field) recomputes the allowed
/// operators (<see cref="AssignmentOperatorPolicy"/>) and resets an
/// out-of-policy operator. Persisted shape is <see cref="AssignmentCondition"/>.
/// </summary>
public sealed partial class AssignmentConditionRowViewModel : ObservableObject
{
    private readonly IReadOnlyList<AssignmentOperatorItem> _attributeOperators;
    private readonly IReadOnlyList<AssignmentOperatorItem> _ordinalOperators;
    private readonly IReadOnlyList<AssignmentOperatorItem> _textOperators;

    [ObservableProperty]
    private AssignmentConditionSourceKind _sourceKind;

    [ObservableProperty]
    private string? _selectedAttributeId;

    [ObservableProperty]
    private AssignmentSystemField? _systemField;

    [ObservableProperty]
    private ValidationRuleOperator _operator;

    [ObservableProperty]
    private string? _valueText;

    [ObservableProperty]
    private string? _valueNumberText;

    [ObservableProperty]
    private string? _minValueText;

    [ObservableProperty]
    private string? _maxValueText;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private IReadOnlyList<AssignmentOperatorItem> _availableOperators;

    private readonly IReadOnlyList<AssignmentValueItem> _revitCategories;
    private readonly IReadOnlyList<AssignmentValueItem> _partTypes;

    public string? Id { get; }

    /// <summary>Revit category list for the picker ComboBox
    /// (SelectedValuePath=Key → <see cref="ValueText"/>).</summary>
    public IReadOnlyList<AssignmentValueItem> RevitCategories => _revitCategories;

    /// <summary>Part Type list for the picker ComboBox (same pattern).</summary>
    public IReadOnlyList<AssignmentValueItem> PartTypes => _partTypes;

    public AssignmentConditionRowViewModel(
        string? id,
        AssignmentConditionSourceKind sourceKind,
        string? attributeId,
        AssignmentSystemField? systemField,
        ValidationRuleOperator op,
        string? valueText,
        double? valueNumber,
        double? minValue,
        double? maxValue,
        bool isEnabled,
        IReadOnlyList<AssignmentOperatorItem> attributeOperators,
        IReadOnlyList<AssignmentOperatorItem> ordinalOperators,
        IReadOnlyList<AssignmentOperatorItem> textOperators,
        IReadOnlyList<AssignmentValueItem>? revitCategories = null,
        IReadOnlyList<AssignmentValueItem>? partTypes = null)
    {
        Id = id;
        _attributeOperators = attributeOperators;
        _ordinalOperators = ordinalOperators;
        _textOperators = textOperators;
        _revitCategories = revitCategories ?? [];
        _partTypes = partTypes ?? [];
        _sourceKind = sourceKind;
        _selectedAttributeId = attributeId;
        _systemField = systemField;
        _operator = op;
        _isEnabled = isEnabled;
        _availableOperators = OperatorsFor(sourceKind, systemField);

        if (sourceKind == AssignmentConditionSourceKind.System && systemField is AssignmentSystemField.RevitCategory or AssignmentSystemField.PartType)
        {
            _valueText = valueText;
        }
        else if (op is ValidationRuleOperator.Between)
        {
            _minValueText = minValue?.ToString("G15", CultureInfo.InvariantCulture);
            _maxValueText = maxValue?.ToString("G15", CultureInfo.InvariantCulture);
        }
        else
        {
            _valueText = valueText ?? valueNumber?.ToString("G15", CultureInfo.InvariantCulture);
        }
    }

    public bool IsAttributeSource => SourceKind == AssignmentConditionSourceKind.Attribute;

    /// <summary>ComboBox-friendly proxy for <see cref="SourceKind"/>
    /// (SelectedIndex 0 = attribute, 1 = system field).</summary>
    public int SourceKindIndex
    {
        get => SourceKind == AssignmentConditionSourceKind.System ? 1 : 0;
        set => SourceKind = value == 1 ? AssignmentConditionSourceKind.System : AssignmentConditionSourceKind.Attribute;
    }

    public bool IsOrdinalSystemField =>
        SourceKind == AssignmentConditionSourceKind.System
        && SystemField is AssignmentSystemField.RevitCategory or AssignmentSystemField.PartType;

    /// <summary>Text value field — only for operators whose engine
    /// evaluation reads ValueText: IsPresent / HasValue are evaluated on
    /// the parameter itself (FamilyValidationEngine ignores rule.ValueText).
    /// Audit #241: forcing a value here blocked saving a legit rule.</summary>
    public bool ShowValueField =>
        !IsOrdinalSystemField
        && Operator is ValidationRuleOperator.Equals or ValidationRuleOperator.NotEquals or ValidationRuleOperator.Contains;

    public bool ShowNumberField => !IsOrdinalSystemField && Operator is ValidationRuleOperator.GreaterThan or ValidationRuleOperator.GreaterOrEqual or ValidationRuleOperator.LessThan or ValidationRuleOperator.LessOrEqual;
    public bool ShowRangeFields => !IsOrdinalSystemField && Operator == ValidationRuleOperator.Between;
    public bool ShowCategoryPicker => SourceKind == AssignmentConditionSourceKind.System && SystemField == AssignmentSystemField.RevitCategory;
    public bool ShowPartTypePicker => SourceKind == AssignmentConditionSourceKind.System && SystemField == AssignmentSystemField.PartType;

    partial void OnSourceKindChanged(AssignmentConditionSourceKind value)
    {
        ClearValueFields();
        OnPropertyChanged(nameof(IsAttributeSource));
        OnPropertyChanged(nameof(SourceKindIndex));
        RecomputeOperators();
    }

    partial void OnSystemFieldChanged(AssignmentSystemField? value)
    {
        if (SourceKind == AssignmentConditionSourceKind.System)
        {
            // A stored value is never transferable between system fields
            // (a Revit category ordinal is not a Part Type ordinal nor a
            // family name) — clear it instead of leaking digits like
            // "-2008049" into the text box.
            ClearValueFields();
            OnPropertyChanged(nameof(IsOrdinalSystemField));
            OnPropertyChanged(nameof(ShowCategoryPicker));
            OnPropertyChanged(nameof(ShowPartTypePicker));
            RecomputeOperators();
        }
    }

    private void ClearValueFields()
    {
        ValueText = null;
        ValueNumberText = null;
        MinValueText = null;
        MaxValueText = null;
    }

    partial void OnOperatorChanged(ValidationRuleOperator value)
    {
        OnPropertyChanged(nameof(ShowValueField));
        OnPropertyChanged(nameof(ShowNumberField));
        OnPropertyChanged(nameof(ShowRangeFields));
    }

    private void RecomputeOperators()
    {
        AvailableOperators = OperatorsFor(SourceKind, SystemField);
        if (SystemField is AssignmentSystemField.RevitCategory or AssignmentSystemField.PartType)
        {
            OnPropertyChanged(nameof(IsOrdinalSystemField));
            OnPropertyChanged(nameof(ShowCategoryPicker));
            OnPropertyChanged(nameof(ShowPartTypePicker));
        }
        OnPropertyChanged(nameof(ShowValueField));
        OnPropertyChanged(nameof(ShowNumberField));
        OnPropertyChanged(nameof(ShowRangeFields));

        if (AvailableOperators.All(o => o.Operator != Operator) && AvailableOperators.Count > 0)
        {
            Operator = AvailableOperators[0].Operator;
        }
    }

    private IReadOnlyList<AssignmentOperatorItem> OperatorsFor(AssignmentConditionSourceKind sourceKind, AssignmentSystemField? field) =>
        sourceKind == AssignmentConditionSourceKind.Attribute
            ? _attributeOperators
            : field is AssignmentSystemField.FamilyName or AssignmentSystemField.SystemFamilyKey
                ? _textOperators
                : _ordinalOperators;
}

/// <summary>ComboBox item: operator + localized display name (reuses the
/// validation editor's operator labels).</summary>
public sealed record AssignmentOperatorItem(ValidationRuleOperator Operator, string DisplayName);

/// <summary>ComboBox item: system field + localized display name.</summary>
public sealed record AssignmentSystemFieldItem(AssignmentSystemField Field, string DisplayName);

/// <summary>ComboBox item: invariant ordinal-string key + localized label
/// (Revit category / Part Type pickers).</summary>
public sealed record AssignmentValueItem(string Key, string Label);

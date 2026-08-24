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

    [ObservableProperty]
    private string _categorySearchText = string.Empty;

    [ObservableProperty]
    private string _partTypeSearchText = string.Empty;

    [ObservableProperty]
    private bool _isCategoryPopupOpen;

    [ObservableProperty]
    private bool _isPartTypePopupOpen;

    public string? Id { get; }

    public IReadOnlyList<AssignmentValueItem> FilteredRevitCategories => FilterValues(_revitCategories, CategorySearchText);
    public IReadOnlyList<AssignmentValueItem> FilteredPartTypes => FilterValues(_partTypes, PartTypeSearchText);

    /// <summary>Selected Revit category label (displayed on the picker
    /// button); empty when nothing is selected.</summary>
    public string SelectedCategoryLabel => FindLabel(_revitCategories, ValueText);

    /// <summary>Selected Part Type label (displayed on the picker button).</summary>
    public string SelectedPartTypeLabel => FindLabel(_partTypes, ValueText);

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

    partial void OnCategorySearchTextChanged(string value) =>
        OnPropertyChanged(nameof(FilteredRevitCategories));

    partial void OnPartTypeSearchTextChanged(string value) =>
        OnPropertyChanged(nameof(FilteredPartTypes));

    partial void OnIsCategoryPopupOpenChanged(bool value)
    {
        if (value) CategorySearchText = string.Empty;
    }

    partial void OnIsPartTypePopupOpenChanged(bool value)
    {
        if (value) PartTypeSearchText = string.Empty;
    }

    /// <summary>Selection proxy for the category ListBox: only a real user
    /// pick writes <see cref="ValueText"/> — the filter-driven deselection
    /// (the current item fell out of the filtered list while typing) must
    /// NOT clear the stored value. The getter is always null: the list
    /// shows no selection state by design.</summary>
    public AssignmentValueItem? SelectedCategoryItem
    {
        get => null;
        set
        {
            if (value is null) return;
            ValueText = value.Key;
        }
    }

    /// <summary>Same proxy for the Part Type ListBox.</summary>
    public AssignmentValueItem? SelectedPartTypeItem
    {
        get => null;
        set
        {
            if (value is null) return;
            ValueText = value.Key;
        }
    }

    partial void OnValueTextChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedCategoryLabel));
        OnPropertyChanged(nameof(SelectedPartTypeLabel));
        // A selection closes the popup immediately.
        IsCategoryPopupOpen = false;
        IsPartTypePopupOpen = false;
    }

    private static IReadOnlyList<AssignmentValueItem> FilterValues(
        IReadOnlyList<AssignmentValueItem> source, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return source;
        }

        return source
#if NET8_0_OR_GREATER
            .Where(v => v.Label.Contains(search, StringComparison.OrdinalIgnoreCase))
#else
            .Where(v => v.Label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
#endif
            .ToList();
    }

    private static string FindLabel(IReadOnlyList<AssignmentValueItem> source, string? key) =>
        key is null ? string.Empty
        : source.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.Ordinal))?.Label ?? string.Empty;

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

    public bool ShowValueField => !IsOrdinalSystemField && Operator is not ValidationRuleOperator.Between;
    public bool ShowNumberField => !IsOrdinalSystemField && Operator is ValidationRuleOperator.GreaterThan or ValidationRuleOperator.GreaterOrEqual or ValidationRuleOperator.LessThan or ValidationRuleOperator.LessOrEqual;
    public bool ShowRangeFields => !IsOrdinalSystemField && Operator == ValidationRuleOperator.Between;
    public bool ShowCategoryPicker => SourceKind == AssignmentConditionSourceKind.System && SystemField == AssignmentSystemField.RevitCategory;
    public bool ShowPartTypePicker => SourceKind == AssignmentConditionSourceKind.System && SystemField == AssignmentSystemField.PartType;

    partial void OnSourceKindChanged(AssignmentConditionSourceKind value)
    {
        OnPropertyChanged(nameof(IsAttributeSource));
        OnPropertyChanged(nameof(SourceKindIndex));
        RecomputeOperators();
    }

    partial void OnSystemFieldChanged(AssignmentSystemField? value)
    {
        if (SourceKind == AssignmentConditionSourceKind.System)
        {
            OnPropertyChanged(nameof(IsOrdinalSystemField));
            OnPropertyChanged(nameof(ShowCategoryPicker));
            OnPropertyChanged(nameof(ShowPartTypePicker));
            RecomputeOperators();
        }
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

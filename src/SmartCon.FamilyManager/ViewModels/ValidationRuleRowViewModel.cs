using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// One editable validation rule row in the rules editor dialog.
/// Numeric operators validate their input as a number on save; Equals/
/// NotEquals auto-detect number-vs-text from the input.
/// </summary>
public sealed partial class ValidationRuleRowViewModel : ObservableObject
{
    public string? Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowValueField))]
    [NotifyPropertyChangedFor(nameof(ShowNumberField))]
    [NotifyPropertyChangedFor(nameof(ShowRangeFields))]
    private ValidationRuleOperator _operator;

    [ObservableProperty]
    private string? _value;

    [ObservableProperty]
    private string? _minValue;

    [ObservableProperty]
    private string? _maxValue;

    [ObservableProperty]
    private bool _isEnabled = true;

    public int SortOrder { get; set; }

    public bool ShowValueField =>
        Operator is ValidationRuleOperator.Equals
            or ValidationRuleOperator.NotEquals
            or ValidationRuleOperator.Contains
            or ValidationRuleOperator.NotContains;

    public bool ShowNumberField =>
        Operator is ValidationRuleOperator.GreaterThan
            or ValidationRuleOperator.GreaterOrEqual
            or ValidationRuleOperator.LessThan
            or ValidationRuleOperator.LessOrEqual;

    public bool ShowRangeFields => Operator == ValidationRuleOperator.Between;

    public ValidationRuleRowViewModel(string? id, ValidationRuleOperator op,
        string? value, string? minValue, string? maxValue, bool isEnabled, int sortOrder)
    {
        Id = id;
        _operator = op;
        _value = value;
        _minValue = minValue;
        _maxValue = maxValue;
        _isEnabled = isEnabled;
        SortOrder = sortOrder;
    }
}

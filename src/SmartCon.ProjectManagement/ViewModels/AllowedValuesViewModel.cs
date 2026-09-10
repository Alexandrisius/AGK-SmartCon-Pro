using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.ProjectManagement.ViewModels;

public sealed partial class ValueItem : ObservableObject
{
    [ObservableProperty]
    private string _value = string.Empty;
}

public sealed partial class AllowedValuesViewModel : ObservableObject, IObservableRequestClose
{
    [ObservableProperty]
    private ValidationMode _validationMode;

    [ObservableProperty]
    private int? _minLength;

    [ObservableProperty]
    private int? _maxLength;

    [ObservableProperty]
    private ValueItem? _selectedValue;

    [ObservableProperty]
    private ValueItem? _focusItem;

    public ObservableCollection<ValueItem> Values { get; } = [];

    public List<EnumOption<ValidationMode>> ValidationModeOptions { get; }

    private EnumOption<ValidationMode> _selectedValidationModeOption;

    public EnumOption<ValidationMode> SelectedValidationModeOption
    {
        get => _selectedValidationModeOption;
        set
        {
            if (SetProperty(ref _selectedValidationModeOption, value) && value is not null)
            {
                ValidationMode = value.Value;
                OnPropertyChanged(nameof(ShowValuesList));
                OnPropertyChanged(nameof(ShowLengthFields));
            }
        }
    }

    public bool ShowValuesList => ValidationMode is ValidationMode.AllowedValues or ValidationMode.Contains;
    public bool ShowLengthFields => ValidationMode == ValidationMode.CharCount;

    public event Action<bool?>? RequestClose;

    public AllowedValuesViewModel(FieldDefinitionItem fieldItem)
    {
        _validationMode = fieldItem.ValidationMode;
        _minLength = fieldItem.MinLength;
        _maxLength = fieldItem.MaxLength;

        ValidationModeOptions =
        [
            new() { Value = ValidationMode.None, Display = LocalizationService.GetString("PM_ValMode_None"), Description = LocalizationService.GetString("PM_ValMode_None_Desc") },
            new() { Value = ValidationMode.AllowedValues, Display = LocalizationService.GetString("PM_ValMode_List"), Description = LocalizationService.GetString("PM_ValMode_List_Desc") },
            new() { Value = ValidationMode.Contains, Display = LocalizationService.GetString("PM_ValMode_Contains"), Description = LocalizationService.GetString("PM_ValMode_Contains_Desc") },
            new() { Value = ValidationMode.CharCount, Display = LocalizationService.GetString("PM_ValMode_Length"), Description = LocalizationService.GetString("PM_ValMode_Length_Desc") }
        ];

        _selectedValidationModeOption = ValidationModeOptions.First(o => o.Value == _validationMode);

        foreach (var v in fieldItem.AllowedValues)
            Values.Add(new ValueItem { Value = v });
    }

    partial void OnValidationModeChanged(ValidationMode value)
    {
        _selectedValidationModeOption = ValidationModeOptions.First(o => o.Value == value);
        OnPropertyChanged(nameof(SelectedValidationModeOption));
        OnPropertyChanged(nameof(ShowValuesList));
        OnPropertyChanged(nameof(ShowLengthFields));
    }

    public void ApplyTo(FieldDefinitionItem target)
    {
        target.ValidationMode = ValidationMode;
        target.MinLength = MinLength;
        target.MaxLength = MaxLength;
        target.AllowedValues = Values.Select(v => v.Value.Trim()).ToList();
    }

    [RelayCommand]
    private void AddValue()
    {
        var item = new ValueItem();
        FocusItem = null;
        Values.Add(item);
        SelectedValue = item;
        FocusItem = item;
    }

    [RelayCommand]
    private void RemoveValue()
    {
        if (SelectedValue is null)
            return;

        Values.Remove(SelectedValue);
        SelectedValue = null;
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (SelectedValue is null)
            return;

        var index = Values.IndexOf(SelectedValue);
        if (index <= 0)
            return;

        Values.Move(index, index - 1);
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (SelectedValue is null)
            return;

        var index = Values.IndexOf(SelectedValue);
        if (index < 0 || index >= Values.Count - 1)
            return;

        Values.Move(index, index + 1);
    }

    [RelayCommand]
    private void Ok()
    {
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}

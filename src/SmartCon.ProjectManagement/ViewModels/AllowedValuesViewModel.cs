using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.ProjectManagement.ViewModels;

public sealed partial class AllowedValuesViewModel : ObservableObject, IObservableRequestClose
{
    [ObservableProperty]
    private ValidationMode _validationMode;

    [ObservableProperty]
    private int? _minLength;

    [ObservableProperty]
    private int? _maxLength;

    [ObservableProperty]
    private string _newValue = string.Empty;

    [ObservableProperty]
    private int _selectedIndex = -1;

    public ObservableCollection<string> Values { get; } = [];

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

    public bool ShowValuesList => ValidationMode == ValidationMode.AllowedValues;
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
            new() { Value = ValidationMode.CharCount, Display = LocalizationService.GetString("PM_ValMode_Length"), Description = LocalizationService.GetString("PM_ValMode_Length_Desc") }
        ];

        _selectedValidationModeOption = ValidationModeOptions.First(o => o.Value == _validationMode);

        foreach (var v in fieldItem.AllowedValues)
            Values.Add(v);
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
        target.AllowedValues = Values.ToList();
    }

    [RelayCommand]
    private void AddValue()
    {
        if (string.IsNullOrWhiteSpace(NewValue)) return;
        Values.Add(NewValue.Trim());
        NewValue = string.Empty;
    }

    [RelayCommand]
    private void RemoveValue()
    {
        if (SelectedIndex >= 0 && SelectedIndex < Values.Count)
            Values.RemoveAt(SelectedIndex);
    }

    [RelayCommand]
    private void Save()
    {
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models;
using SmartCon.Core.Services;

namespace SmartCon.ProjectManagement.ViewModels;

public sealed partial class FieldDefinitionItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private ValidationMode _validationMode;

    [ObservableProperty]
    private List<string> _allowedValues = [];

    [ObservableProperty]
    private int? _minLength;

    [ObservableProperty]
    private int? _maxLength;

    public string ValidationModeDisplay => ValidationMode switch
    {
        ValidationMode.None => LocalizationService.GetString("PM_ValMode_None") ?? "Any",
        ValidationMode.AllowedValues => LocalizationService.GetString("PM_ValMode_List") ?? "List",
        ValidationMode.CharCount => LocalizationService.GetString("PM_ValMode_Length") ?? "Length",
        _ => ValidationMode.ToString()
    };

    partial void OnValidationModeChanged(ValidationMode value)
    {
        OnPropertyChanged(nameof(ValidationModeDisplay));
    }

    public void RefreshValidationModeDisplay()
    {
        OnPropertyChanged(nameof(ValidationModeDisplay));
    }

    public FieldDefinition ToModel()
    {
        return new FieldDefinition
        {
            Name = Name,
            DisplayName = DisplayName,
            Description = Description,
            ValidationMode = ValidationMode,
            AllowedValues = AllowedValues,
            MinLength = MinLength,
            MaxLength = MaxLength
        };
    }

    public static FieldDefinitionItem FromModel(FieldDefinition model)
    {
        return new FieldDefinitionItem
        {
            Name = model.Name,
            DisplayName = model.DisplayName,
            Description = model.Description,
            ValidationMode = model.ValidationMode,
            AllowedValues = model.AllowedValues,
            MinLength = model.MinLength,
            MaxLength = model.MaxLength
        };
    }
}

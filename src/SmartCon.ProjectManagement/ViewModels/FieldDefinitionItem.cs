using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models;

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

    [ObservableProperty]
    private string _validationModeDisplay = string.Empty;

    partial void OnValidationModeChanged(ValidationMode value)
    {
        UpdateValidationModeDisplay();
    }

    public void UpdateValidationModeDisplay()
    {
        ValidationModeDisplay = ValidationMode switch
        {
            ValidationMode.None => "Любое",
            ValidationMode.AllowedValues => "Из списка",
            ValidationMode.CharCount => "Длина",
            ValidationMode.AllowedValuesAndCharCount => "Список + Длина",
            _ => ValidationMode.ToString()
        };
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
        var item = new FieldDefinitionItem
        {
            Name = model.Name,
            DisplayName = model.DisplayName,
            Description = model.Description,
            ValidationMode = model.ValidationMode,
            AllowedValues = model.AllowedValues,
            MinLength = model.MinLength,
            MaxLength = model.MaxLength
        };
        item.UpdateValidationModeDisplay();
        return item;
    }
}

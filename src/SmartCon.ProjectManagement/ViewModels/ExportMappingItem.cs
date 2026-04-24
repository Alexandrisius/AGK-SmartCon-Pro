using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartCon.ProjectManagement.ViewModels;

public sealed partial class ExportMappingItem : ObservableObject
{
    [ObservableProperty]
    private string _field = string.Empty;

    [ObservableProperty]
    private string _sourceValue = string.Empty;

    [ObservableProperty]
    private string _targetValue = string.Empty;

    [ObservableProperty]
    private string _currentBlockValue = string.Empty;

    [ObservableProperty]
    private bool _isValid = true;

    [ObservableProperty]
    private string? _validationError;

    public ObservableCollection<string> AllowedTargetValues { get; } = [];

    partial void OnFieldChanged(string value)
    {
        if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(SourceValue))
            SourceValue = CurrentBlockValue;
    }
}

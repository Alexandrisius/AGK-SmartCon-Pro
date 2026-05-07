using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class AttributeListItemViewModel : ObservableObject
{
    [ObservableProperty] private string _attributeId = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _group;
    [ObservableProperty] private bool _isBound;
    [ObservableProperty] private bool _isInherited;
    [ObservableProperty] private string? _sourceCategoryName;
    [ObservableProperty] private string? _bindingId;
    [ObservableProperty] private bool _isEnabled = true;

    public bool OriginalIsBound { get; set; }
    public bool IsDirty { get; set; }

    public CategoryTreeEditorViewModel? Parent { get; set; }

    [RelayCommand]
    private void ToggleBinding()
    {
        Parent?.HandleBindingToggle(this, !IsBound);
    }
}

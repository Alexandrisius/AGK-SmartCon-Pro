using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartCon.ProjectManagement.ViewModels;

public sealed partial class ViewSelectionItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public string Name { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string ViewType { get; init; } = string.Empty;
}

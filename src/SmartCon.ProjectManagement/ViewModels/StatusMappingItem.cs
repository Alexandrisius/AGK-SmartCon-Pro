using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartCon.ProjectManagement.ViewModels;

public sealed partial class StatusMappingItem : ObservableObject
{
    [ObservableProperty]
    private string _wipValue = string.Empty;

    [ObservableProperty]
    private string _sharedValue = string.Empty;
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartCon.ProjectManagement.ViewModels;

public sealed partial class FileNameBlockItem : ObservableObject
{
    [ObservableProperty]
    private int _index;

    [ObservableProperty]
    private string _role = string.Empty;

    [ObservableProperty]
    private string _label = string.Empty;
}

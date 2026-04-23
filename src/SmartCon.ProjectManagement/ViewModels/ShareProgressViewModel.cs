using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.ProjectManagement.ViewModels;

public sealed partial class ShareProgressViewModel : ObservableObject, IObservableRequestClose
{
    [ObservableProperty]
    private string _statusText = "Preparing...";

    [ObservableProperty]
    private int _progressValue;

    [ObservableProperty]
    private int _progressMaximum = 100;

    public event Action? RequestClose;
}

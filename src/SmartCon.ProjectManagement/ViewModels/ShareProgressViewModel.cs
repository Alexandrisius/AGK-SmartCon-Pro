using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.ProjectManagement.ViewModels;

public sealed partial class ShareProgressViewModel : ObservableObject, IObservableRequestClose
{
    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private int _progressValue;

    [ObservableProperty]
    private int _progressMaximum = 100;

    public event Action<bool?>? RequestClose;

    public ShareProgressViewModel()
    {
        _statusText = LocalizationService.GetString("PM_Step_Preparing") ?? "Preparing...";
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class AboutViewModel : ObservableObject
{
    private readonly IUpdateService _updateService;
    private readonly IUpdateSettingsRepository _settingsRepo;

    [ObservableProperty]
    private string _currentVersion = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _latestVersionInfo = string.Empty;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private bool _updatePending;

    [ObservableProperty]
    private string _releaseNotes = string.Empty;

    [ObservableProperty]
    private bool _checkOnStartup = true;

    private UpdateInfo? _foundUpdate;

    public AboutViewModel(
        IUpdateService updateService,
        IUpdateSettingsRepository settingsRepo)
    {
        _updateService = updateService;
        _settingsRepo = settingsRepo;

        CurrentVersion = _updateService.GetCurrentVersion();

        var settings = _settingsRepo.Load();
        CheckOnStartup = settings.CheckOnStartup;

        RefreshPendingState();
    }

    [RelayCommand]
    private async Task CheckForUpdate()
    {
        IsChecking = true;
        StatusMessage = "Checking for updates...";
        UpdateAvailable = false;
        LatestVersionInfo = string.Empty;
        ReleaseNotes = string.Empty;

        try
        {
            _foundUpdate = await _updateService.CheckForUpdateAsync();

            if (_foundUpdate is null)
            {
                StatusMessage = $"v{CurrentVersion} is up to date.";
                LatestVersionInfo = $"Current: v{CurrentVersion} (latest)";
            }
            else
            {
                UpdateAvailable = true;
                LatestVersionInfo = $"Available: v{_foundUpdate.Version} (current: v{CurrentVersion})";
                ReleaseNotes = _foundUpdate.ReleaseNotes ?? string.Empty;
                StatusMessage = $"v{_foundUpdate.Version} available. Click Download.";
            }
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAndStage()
    {
        if (_foundUpdate is null) return;

        IsDownloading = true;
        DownloadProgress = 0;
        StatusMessage = "Downloading...";

        var progress = new Progress<double>(p => DownloadProgress = p * 100);

        try
        {
            var zipPath = await _updateService.DownloadUpdateAsync(_foundUpdate, progress);
            await _updateService.StageUpdateAsync(zipPath);

            UpdatePending = true;
            UpdateAvailable = false;
            StatusMessage = $"v{_foundUpdate.Version} will be installed when Revit closes.";
            _foundUpdate = null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download error: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    partial void OnCheckOnStartupChanged(bool value)
    {
        var settings = _settingsRepo.Load();
        _settingsRepo.Save(settings with { CheckOnStartup = value });
    }

    private void RefreshPendingState()
    {
        try
        {
            var pending = _updateService.GetPendingUpdateAsync().GetAwaiter().GetResult();
            UpdatePending = pending is not null;
            if (UpdatePending)
                StatusMessage = "Pending update will be installed when Revit closes.";
        }
        catch
        {
            UpdatePending = false;
        }
    }
}

using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Services;
using SmartCon.UI;

namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class AboutViewModel : ObservableObject, IObservableRequestClose
{
    private readonly IUpdateService _updateService;
    private readonly IUpdateSettingsRepository _settingsRepo;

    [ObservableProperty]
    private string _currentVersion = string.Empty;

    public string VersionDisplay =>
        string.Format(LocalizationService.GetString("About_Version"), CurrentVersion);

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

    [ObservableProperty]
    private int _languageIndex;

    private UpdateInfo? _foundUpdate;

    public event Action? RequestClose;

    public AboutViewModel(
        IUpdateService updateService,
        IUpdateSettingsRepository settingsRepo)
    {
        _updateService = updateService;
        _settingsRepo = settingsRepo;

        CurrentVersion = _updateService.GetCurrentVersion();

        var settings = _settingsRepo.Load();
        CheckOnStartup = settings.CheckOnStartup;

        LanguageIndex = LocalizationService.CurrentLanguage == Language.EN ? 1 : 0;

        LocalizationService.LanguageChanged += () => OnPropertyChanged(nameof(VersionDisplay));

        RefreshPendingState();
    }

    [RelayCommand]
    private async Task CheckForUpdate()
    {
        IsChecking = true;
        StatusMessage = LocalizationService.GetString("About_CheckingUpdates");
        UpdateAvailable = false;
        LatestVersionInfo = string.Empty;
        ReleaseNotes = string.Empty;

        try
        {
            _foundUpdate = await _updateService.CheckForUpdateAsync();

            if (_foundUpdate is null)
            {
                StatusMessage = string.Format(LocalizationService.GetString("About_UpToDate"), CurrentVersion);
                LatestVersionInfo = string.Format(LocalizationService.GetString("About_CurrentLatest"), CurrentVersion);
            }
            else
            {
                UpdateAvailable = true;
                LatestVersionInfo = string.Format(LocalizationService.GetString("About_AvailableVersion"), _foundUpdate.Version, CurrentVersion);
                ReleaseNotes = _foundUpdate.Changelog ?? _foundUpdate.ReleaseNotes ?? string.Empty;
                StatusMessage = string.Format(LocalizationService.GetString("About_VersionAvailable"), _foundUpdate.Version);
            }
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message);
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
        StatusMessage = LocalizationService.GetString("About_Downloading");

        var progress = new Progress<double>(p => DownloadProgress = p * 100);

        try
        {
            var zipPath = await _updateService.DownloadUpdateAsync(_foundUpdate, progress);
            await _updateService.StageUpdateAsync(zipPath);

            UpdatePending = true;
            UpdateAvailable = false;
            StatusMessage = string.Format(LocalizationService.GetString("About_WillInstallOnClose"), _foundUpdate.Version);
            _foundUpdate = null;
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LocalizationService.GetString("About_DownloadError"), ex.Message);
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

    partial void OnLanguageIndexChanged(int value)
    {
        var lang = value == 1 ? Language.EN : Language.RU;
        LanguageManager.SwitchLanguage(lang);
    }

    private void RefreshPendingState()
    {
        try
        {
            var pending = _updateService.GetPendingUpdateAsync().GetAwaiter().GetResult();
            UpdatePending = pending is not null;
            if (UpdatePending)
                StatusMessage = LocalizationService.GetString("About_PendingUpdate");
        }
        catch
        {
            UpdatePending = false;
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    [RelayCommand]
    private void OpenLink(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}

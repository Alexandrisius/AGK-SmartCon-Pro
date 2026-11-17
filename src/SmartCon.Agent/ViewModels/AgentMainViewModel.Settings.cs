using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartCon.Agent.ViewModels;

public sealed partial class AgentMainViewModel
{
    public IReadOnlyList<string> ProviderOptions { get; } = new[] { "openrouter", "opencode-go" };

    public bool HasStoredApiKey => _settings.HasApiKey;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private string _settingsProvider = "openrouter";

    [ObservableProperty]
    private string _settingsModel = string.Empty;

    [ObservableProperty]
    private string _settingsApiKeyInput = string.Empty;

    [ObservableProperty]
    private string _settingsProxy = string.Empty;

    [RelayCommand]
    private void OpenSettings()
    {
        SettingsProvider = _settings.Provider;
        SettingsModel = _settings.Model;
        SettingsProxy = _settings.ProxyUrl;
        SettingsApiKeyInput = string.Empty;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings()
    {
        SettingsApiKeyInput = string.Empty;
        IsSettingsOpen = false;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _settings.Apply(SettingsProvider, SettingsModel, SettingsApiKeyInput, SettingsProxy);
        _settings.Save();

        _hasApiKey = _settings.HasApiKey;
        SettingsApiKeyInput = string.Empty;
        IsSettingsOpen = false;

        OnPropertyChanged(nameof(HasApiKey));
        OnPropertyChanged(nameof(HasStoredApiKey));
        OnPropertyChanged(nameof(ProviderLabel));
        UpdateBanner();

        StatusText = S("Agent_Settings_Saved", "Настройки сохранены");
        HasError = false;
    }
}
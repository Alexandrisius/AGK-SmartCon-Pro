using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartCon.Agent.ViewModels;

public sealed partial class AgentMainViewModel
{
    private static readonly string[] KnownDecisionModels =
    {
        "perplexity/pplx-decider-v1.3:free",
        "perplexity/pplx-decider-latest",
        "jev-1.13.0"
    };

    public IReadOnlyList<string> ProviderOptions { get; } = new[] { "openrouter", "opencode-go" };

    public ObservableCollection<AgentDecisionModelOption> DecisionModelOptions { get; } = new();

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
    private AgentDecisionModelOption? _selectedDecisionModel;

    [RelayCommand]
    private void OpenSettings()
    {
        SettingsProvider = _settings.Provider;
        SettingsModel = _settings.Model;
        SettingsApiKeyInput = string.Empty;
        RebuildDecisionOptions();
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
        _settings.Apply(SettingsProvider, SettingsModel, SettingsApiKeyInput);
        _settings.Save();
        _decisionsSettings.Apply(SelectedDecisionModel?.Value);

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

    private void RebuildDecisionOptions()
    {
        DecisionModelOptions.Clear();
        DecisionModelOptions.Add(new AgentDecisionModelOption(
            null,
            S("Agent_Decision_Off", "Не использовать")));

        foreach (var model in KnownDecisionModels)
            DecisionModelOptions.Add(new AgentDecisionModelOption(model, model));

        var current = _decisionsSettings.Model;
        if (_decisionsSettings.Enabled
            && !KnownDecisionModels.Contains(current, StringComparer.Ordinal))
        {
            DecisionModelOptions.Add(new AgentDecisionModelOption(current, current));
        }

        SelectedDecisionModel = _decisionsSettings.Enabled
            ? DecisionModelOptions.FirstOrDefault(option =>
                string.Equals(option.Value, current, StringComparison.Ordinal))
            : DecisionModelOptions[0];
    }
}

public sealed record AgentDecisionModelOption(string? Value, string Label);
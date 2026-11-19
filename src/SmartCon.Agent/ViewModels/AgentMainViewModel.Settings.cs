using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Agent.Services.Decisions;
using SmartCon.Agent.Services.Llm;

namespace SmartCon.Agent.ViewModels;

public sealed partial class AgentMainViewModel
{
    private static readonly Dictionary<string, string[]> KnownDecisionModels =
        new(StringComparer.Ordinal)
        {
            ["openrouter"] = new[]
            {
                AgentDecisionsSettings.DefaultOpenRouterModel,
                "perplexity/pplx-decider-latest",
                "microsoft/microsoft-decision-1.1"
            },
            ["perplexity"] = new[] { AgentDecisionsSettings.DefaultPerplexityModel },
            ["typesafe"] = new[] { AgentDecisionsSettings.DefaultTypeSafeModel }
        };

    public IReadOnlyList<string> ProviderOptions { get; } = new[] { "openrouter", "opencode-go" };

    public IReadOnlyList<string> DecisionProviderOptions { get; } =
        AgentDecisionsSettings.Providers;

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
    private string _settingsDecisionProvider = "openrouter";

    [ObservableProperty]
    private string _settingsDecisionApiKeyInput = string.Empty;

    [ObservableProperty]
    private AgentDecisionModelOption? _selectedDecisionModel;

    [RelayCommand]
    private void OpenSettings()
    {
        SettingsProvider = _settings.Provider;
        SettingsModel = _settings.Model;
        SettingsApiKeyInput = _settings.ApiKey;
        SettingsDecisionProvider = _decisionsSettings.Provider;
        RebuildDecisionOptions();
        SettingsDecisionApiKeyInput = _decisionsSettings.ApiKey;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings()
    {
        SettingsApiKeyInput = string.Empty;
        SettingsDecisionApiKeyInput = string.Empty;
        IsSettingsOpen = false;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _settings.Apply(SettingsProvider, SettingsModel, SettingsApiKeyInput);
        _settings.Save();
        _decisionsSettings.Apply(
            SettingsDecisionProvider,
            SelectedDecisionModel?.Value,
            SettingsDecisionApiKeyInput);

        _hasApiKey = _settings.HasApiKey;
        SettingsApiKeyInput = string.Empty;
        SettingsDecisionApiKeyInput = string.Empty;
        IsSettingsOpen = false;

        OnPropertyChanged(nameof(HasApiKey));
        OnPropertyChanged(nameof(HasStoredApiKey));
        OnPropertyChanged(nameof(ProviderLabel));
        UpdateBanner();

        StatusText = S("Agent_Settings_Saved", "Настройки сохранены");
        HasError = false;
    }

    partial void OnSettingsProviderChanged(string value)
    {
        if (IsSettingsOpen)
            SettingsApiKeyInput = AgentLlmSettings.GetStoredKeyFor(value);
    }

    partial void OnSettingsDecisionProviderChanged(string value)
    {
        if (!IsSettingsOpen)
            return;

        SettingsDecisionApiKeyInput = AgentDecisionsSettings.GetStoredKeyFor(value);
        RebuildDecisionOptions();
    }

    private void RebuildDecisionOptions()
    {
        DecisionModelOptions.Clear();
        DecisionModelOptions.Add(new AgentDecisionModelOption(
            null,
            S("Agent_Decision_Off", "Не использовать")));

        var provider = AgentDecisionsSettings.NormalizeProvider(SettingsDecisionProvider);
        var known = KnownDecisionModels.TryGetValue(provider, out var models)
            ? models
            : Array.Empty<string>();

        foreach (var model in known)
            DecisionModelOptions.Add(new AgentDecisionModelOption(model, model));

        var current = _decisionsSettings.Model;
        if (_decisionsSettings.Enabled
            && !known.Contains(current, StringComparer.Ordinal))
        {
            DecisionModelOptions.Add(new AgentDecisionModelOption(current, current));
        }

        SelectedDecisionModel = _decisionsSettings.Enabled
            ? DecisionModelOptions.FirstOrDefault(option =>
                string.Equals(option.Value, current, StringComparison.Ordinal))
              ?? DecisionModelOptions[DecisionModelOptions.Count > 1 ? 1 : 0]
            : DecisionModelOptions[0];
    }
}

public sealed record AgentDecisionModelOption(string? Value, string Label);
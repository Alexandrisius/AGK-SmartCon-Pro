using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Agent.Services.Decisions;
using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Logging;

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

    private readonly Dictionary<string, string> _chatKeyDrafts =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _decisionKeyDrafts =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> ProviderOptions { get; } =
        new[] { "openrouter", "opencode-go", "zai-glm" };

    public IReadOnlyList<string> ReasoningEffortOptions { get; } =
        AgentLlmSettings.ReasoningEffortChoices;

    public IReadOnlyList<AgentSettingChoice> ReasoningFormatOptions { get; } = new[]
    {
        new AgentSettingChoice("auto", S("Agent_Settings_FormatAuto", "Auto by provider")),
        new AgentSettingChoice(
            "reasoning_effort",
            S("Agent_Settings_FormatEffort", "reasoning_effort")),
        new AgentSettingChoice(
            "reasoning",
            S("Agent_Settings_FormatObject", "reasoning { effort }")),
        new AgentSettingChoice("none", S("Agent_Settings_FormatNone", "Do not send"))
    };

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
    private string _settingsReasoningEffort = AgentLlmSettings.DefaultReasoningEffort;

    [ObservableProperty]
    private string _settingsReasoningFormat = "auto";

    [ObservableProperty]
    private string _settingsMaxOutputTokens =
        AgentLlmSettings.DefaultMaxOutputTokens.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _settingsDecisionProvider = "openrouter";

    [ObservableProperty]
    private string _settingsDecisionApiKeyInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsError))]
    private string _settingsError = string.Empty;

    [ObservableProperty]
    private AgentDecisionModelOption? _selectedDecisionModel;

    public bool HasSettingsError => !string.IsNullOrWhiteSpace(SettingsError);

    [RelayCommand]
    private void OpenSettings()
    {
        _chatKeyDrafts.Clear();
        _decisionKeyDrafts.Clear();
        SettingsError = string.Empty;

        SettingsProvider = _settings.Provider;
        SettingsModel = _settings.Model;
        SettingsApiKeyInput = _settings.ApiKey;
        _chatKeyDrafts[NormalizeChatProvider(SettingsProvider)] = SettingsApiKeyInput;

        SettingsReasoningEffort = _settings.ReasoningEffort;
        SettingsReasoningFormat = AgentLlmSettings.ReasoningFormatChoiceFor(
            _settings.ReasoningFormat);
        SettingsMaxOutputTokens = _settings.MaxOutputTokens.ToString(
            CultureInfo.InvariantCulture);

        SettingsDecisionProvider = _decisionsSettings.Provider;
        RebuildDecisionOptions();
        SettingsDecisionApiKeyInput = _decisionsSettings.ApiKey;
        _decisionKeyDrafts[NormalizeDecisionProvider(SettingsDecisionProvider)] =
            SettingsDecisionApiKeyInput;

        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings()
    {
        SettingsApiKeyInput = string.Empty;
        SettingsDecisionApiKeyInput = string.Empty;
        _chatKeyDrafts.Clear();
        _decisionKeyDrafts.Clear();
        SettingsError = string.Empty;
        IsSettingsOpen = false;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        using var _scope = SmartConLogger.BeginScope("Agent", ("Method", nameof(SaveSettings)));

        var chatProvider = NormalizeChatProvider(SettingsProvider);
        var chatKey = ResolveChatKey(chatProvider);
        if (string.IsNullOrWhiteSpace(chatKey))
        {
            SettingsError = S(
                "Agent_Settings_KeyRequired",
                "Укажите API-ключ провайдера — без него агент не сможет отправить запрос.");
            SmartConLogger.Warn(
                "Agent settings save blocked: no API key for provider '" + chatProvider + "' " +
                "[Action: enter the provider key in the agent settings]");
            return;
        }

        var decisionProvider = NormalizeDecisionProvider(SettingsDecisionProvider);
        var decisionEnabled = !string.IsNullOrWhiteSpace(SelectedDecisionModel?.Value);
        var decisionKey = ResolveDecisionKey(decisionProvider, chatProvider, chatKey);
        if (decisionEnabled && string.IsNullOrWhiteSpace(decisionKey))
        {
            SettingsError = S(
                "Agent_Settings_DecisionKeyRequired",
                "Для выбранной модели поиска инструментов нужен API-ключ " +
                "или выберите «Не использовать».");
            SmartConLogger.Warn(
                "Agent settings save blocked: decision model '" + SelectedDecisionModel?.Value +
                "' has no API key for provider '" + decisionProvider + "' " +
                "[Action: enter the decision key or turn the decision model off]");
            return;
        }

        if (!int.TryParse(
                SettingsMaxOutputTokens,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var maxOutputTokens)
            || maxOutputTokens < AgentLlmSettings.MinMaxOutputTokens
            || maxOutputTokens > AgentLlmSettings.MaxMaxOutputTokens)
        {
            SettingsError = S(
                "Agent_Settings_MaxOutputTokensInvalid",
                "Макс. токенов ответа: целое число от 256 до 262144.");
            SmartConLogger.Warn(
                "Agent settings save blocked: max output tokens '" + SettingsMaxOutputTokens +
                "' is not in [" + AgentLlmSettings.MinMaxOutputTokens + ", " +
                AgentLlmSettings.MaxMaxOutputTokens + "] " +
                "[Action: enter a valid token budget in the agent settings]");
            return;
        }

        _settings.ReasoningEffort = AgentLlmSettings.NormalizeReasoningEffort(
            SettingsReasoningEffort);
        _settings.ReasoningFormat = AgentLlmSettings.ParseReasoningFormatChoice(
            SettingsReasoningFormat);
        _settings.MaxOutputTokens = maxOutputTokens;
        _settings.Apply(chatProvider, SettingsModel, chatKey);
        if (!_settings.Save())
        {
            SettingsError = S(
                "Agent_Settings_SaveFailed",
                "Не удалось записать ключи в профиль (%APPDATA%\\AGK\\SmartCon\\Agent). " +
                "Проверьте доступ к папке и повторите сохранение.");
            SmartConLogger.Warn(
                "Agent settings save failed: secrets.env is not writable " +
                "(provider='" + chatProvider + "') " +
                "[Action: check write access to %APPDATA%\\AGK\\SmartCon\\Agent and retry]");
            return;
        }

        _decisionsSettings.Apply(
            decisionProvider,
            SelectedDecisionModel?.Value,
            decisionEnabled ? decisionKey : null);

        _hasApiKey = _settings.HasApiKey;
        SettingsApiKeyInput = string.Empty;
        SettingsDecisionApiKeyInput = string.Empty;
        _chatKeyDrafts.Clear();
        _decisionKeyDrafts.Clear();
        SettingsError = string.Empty;
        IsSettingsOpen = false;

        OnPropertyChanged(nameof(HasApiKey));
        OnPropertyChanged(nameof(HasStoredApiKey));
        OnPropertyChanged(nameof(ProviderLabel));
        UpdateBanner();

        StatusText = S("Agent_Settings_Saved", "Настройки сохранены");
        HasError = false;

        SmartConLogger.Info(
            "Agent settings saved: provider=" + _settings.Provider +
            ", model=" + _settings.Model +
            ", hasChatKey=" + _settings.HasApiKey +
            ", reasoning=" + _settings.EffectiveReasoningFormat +
            "/" + _settings.ReasoningEffort +
            ", maxOutputTokens=" + _settings.MaxOutputTokens +
            ", decisionEnabled=" + _decisionsSettings.Enabled +
            ", hasDecisionKey=" + _decisionsSettings.HasApiKey);
    }

    partial void OnSettingsProviderChanged(string? oldValue, string newValue)
    {
        if (!IsSettingsOpen)
            return;

        if (!string.IsNullOrWhiteSpace(oldValue))
            _chatKeyDrafts[NormalizeChatProvider(oldValue)] = SettingsApiKeyInput;

        var provider = NormalizeChatProvider(newValue);
        SettingsApiKeyInput = _chatKeyDrafts.TryGetValue(provider, out var draft)
            ? draft
            : AgentLlmSettings.GetStoredKeyFor(provider);

        // Follow the provider default until the user edits the model by hand:
        // switching OpenRouter <-> OpenCode Go otherwise keeps an id the new
        // provider does not know.
        if (string.IsNullOrWhiteSpace(SettingsModel)
            || string.Equals(
                SettingsModel.Trim(),
                AgentLlmSettings.DefaultModelFor(oldValue),
                StringComparison.Ordinal))
        {
            SettingsModel = AgentLlmSettings.DefaultModelFor(provider);
        }

        SettingsError = string.Empty;
    }

    partial void OnSettingsDecisionProviderChanged(string? oldValue, string newValue)
    {
        if (!IsSettingsOpen)
            return;

        if (!string.IsNullOrWhiteSpace(oldValue))
        {
            _decisionKeyDrafts[NormalizeDecisionProvider(oldValue)] =
                SettingsDecisionApiKeyInput;
        }

        var provider = NormalizeDecisionProvider(newValue);
        SettingsDecisionApiKeyInput = _decisionKeyDrafts.TryGetValue(provider, out var draft)
            ? draft
            : AgentDecisionsSettings.GetStoredKeyFor(provider);
        SettingsError = string.Empty;
        RebuildDecisionOptions();
    }

    partial void OnSettingsApiKeyInputChanged(string value) => SettingsError = string.Empty;

    partial void OnSettingsMaxOutputTokensChanged(string value) => SettingsError = string.Empty;

    partial void OnSettingsDecisionApiKeyInputChanged(string value) =>
        SettingsError = string.Empty;

    private string ResolveChatKey(string provider) =>
        !string.IsNullOrWhiteSpace(SettingsApiKeyInput)
            ? SettingsApiKeyInput.Trim()
            : AgentLlmSettings.GetStoredKeyFor(provider);

    private string ResolveDecisionKey(
        string decisionProvider,
        string chatProvider,
        string chatKey)
    {
        if (!string.IsNullOrWhiteSpace(SettingsDecisionApiKeyInput))
            return SettingsDecisionApiKeyInput.Trim();

        var stored = AgentDecisionsSettings.GetStoredKeyFor(decisionProvider);
        if (!string.IsNullOrWhiteSpace(stored))
            return stored;

        // The decision layer falls back to the chat key when both use the same
        // provider; the key typed in this dialog is not on disk yet, so resolve
        // it from the chat field explicitly.
        return string.Equals(decisionProvider, chatProvider, StringComparison.Ordinal)
            ? chatKey
            : string.Empty;
    }

    private static string NormalizeChatProvider(string? provider) =>
        string.IsNullOrWhiteSpace(provider) ? "openrouter" : provider!.Trim();

    private static string NormalizeDecisionProvider(string? provider) =>
        AgentDecisionsSettings.NormalizeProvider(provider);

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

public sealed record AgentSettingChoice(string Value, string Label);
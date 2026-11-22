namespace SmartCon.Agent.Services.Llm;

/// <summary>
/// Shape of the reasoning request parameter of a provider's API format. The
/// value is a property of the provider, never of a model name.
/// </summary>
public enum AgentReasoningFormat
{
    /// <summary>Send no reasoning parameter at all.</summary>
    None = 0,

    /// <summary>Top-level <c>reasoning_effort</c> string.</summary>
    ReasoningEffort = 1,

    /// <summary>Top-level OpenRouter-style <c>reasoning</c> object.</summary>
    ReasoningObject = 2
}

public sealed class AgentLlmSettings
{
    public const string DefaultOpenRouterBaseUrl = "https://openrouter.ai/api/v1";
    public const string DefaultOpenCodeGoBaseUrl = "https://opencode.ai/inference/go/openai/v1";
    public const string DefaultZaiBaseUrl = "https://api.z.ai/api/coding/paas/v4";
    public const string DefaultModel = "deepseek/deepseek-v4.1-flash-1017";
    public const string DefaultOpenCodeGoModel = "deepseek-v4.1-flash";
    public const string DefaultZaiModel = "glm-5.3";
    public const string DefaultReasoningEffort = "max";
    public const int DefaultMaxOutputTokens = 64_000;
    public const int MinMaxOutputTokens = 256;
    public const int MaxMaxOutputTokens = 262_144;
    public const int DefaultMaxRequestSeconds = 1_800;

    /// <summary>
    /// Effort ladder from the strongest to the weakest value providers accept;
    /// used both for validation and for graceful degradation on HTTP 400.
    /// </summary>
    public static readonly string[] ReasoningEffortLadder =
        { "max", "xhigh", "high", "medium", "low", "minimal" };

    public static readonly string[] ReasoningEffortChoices =
        { "max", "xhigh", "high", "medium", "low", "minimal", "none" };

    public string Provider { get; set; } = "openrouter";

    public string Model { get; set; } = DefaultModel;

    public string BaseUrl { get; set; } = DefaultOpenRouterBaseUrl;

    public string ApiKey { get; set; } = string.Empty;

    public double Temperature { get; set; } = 0.2;

    public int MaxOutputTokens { get; set; } = DefaultMaxOutputTokens;

    /// <summary>
    /// Idle timeout: the maximum gap between two data chunks of the streamed
    /// response (and the maximum wait for the response headers). A whole
    /// request is additionally bounded by <see cref="MaxRequestSeconds"/>.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Absolute per-attempt safety cap, in seconds.</summary>
    public int MaxRequestSeconds { get; set; } = DefaultMaxRequestSeconds;

    /// <summary>
    /// Requested reasoning format; null means "auto": the provider's default
    /// (<see cref="DefaultReasoningFormatFor"/>). Auto re-resolves whenever the
    /// provider changes.
    /// </summary>
    public AgentReasoningFormat? ReasoningFormat { get; set; }

    public string ReasoningEffort { get; set; } = DefaultReasoningEffort;

    public AgentReasoningFormat EffectiveReasoningFormat =>
        ReasoningFormat ?? DefaultReasoningFormatFor(Provider);

    /// <summary>
    /// Working context window used by the history compactor, in tokens. The model
    /// has a 1M window; the owner confirmed good quality at ~500k. Override with
    /// SMARTCON_AGENT_CONTEXT_TOKENS (50k..2M).
    /// </summary>
    public int ContextWindowTokens { get; set; } = 500_000;

    /// <summary>
    /// Share of <see cref="ContextWindowTokens"/> after which old tool results are
    /// compacted. 1.0 means "at the window" (minus the output and status reserve);
    /// override with SMARTCON_AGENT_CONTEXT_THRESHOLD (0.3..1.0).
    /// </summary>
    public double ContextThreshold { get; set; } = 1.0;

    /// <summary>
    /// Model used for history summaries when the context ladder reaches the
    /// summary step. Empty keeps <see cref="Model"/>.
    /// </summary>
    public string SummaryModel { get; set; } = string.Empty;

    /// <summary>
    /// Maximum tokens of one summarizer input chunk; longer histories are
    /// summarized recursively in chunks of this size.
    /// </summary>
    public int SummaryMaxInputTokens { get; set; } = 24_000;

    /// <summary>
    /// Maximum tokens of the compacted summary that replaces old history.
    /// </summary>
    public int SummaryMaxOutputTokens { get; set; } = 1_500;

    public bool IsOpenCodeGo =>
        string.Equals(Provider, "opencode-go", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Provider, "opencode", StringComparison.OrdinalIgnoreCase);

    public static bool IsOpenCodeGoProvider(string? provider) =>
        string.Equals(provider, "opencode-go", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "opencode", StringComparison.OrdinalIgnoreCase);

    /// <summary>Z.ai GLM Coding Plan: OpenAI-compatible coding endpoint.</summary>
    public static bool IsZaiProvider(string? provider) =>
        string.Equals(provider, "zai-glm", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "zai", StringComparison.OrdinalIgnoreCase);

    public static string DefaultModelFor(string? provider) =>
        IsOpenCodeGoProvider(provider) ? DefaultOpenCodeGoModel
        : IsZaiProvider(provider) ? DefaultZaiModel
        : DefaultModel;

    public static string DefaultBaseUrlFor(string? provider) =>
        IsOpenCodeGoProvider(provider) ? DefaultOpenCodeGoBaseUrl
        : IsZaiProvider(provider) ? DefaultZaiBaseUrl
        : DefaultOpenRouterBaseUrl;

    /// <summary>
    /// Reasoning parameter shape of the provider's API format: OpenRouter
    /// documents the <c>reasoning</c> object, the OpenAI-compatible endpoints
    /// (Z.ai, OpenCode Go and unknown providers) use <c>reasoning_effort</c>.
    /// </summary>
    public static AgentReasoningFormat DefaultReasoningFormatFor(string? provider) =>
        string.Equals(provider?.Trim(), "openrouter", StringComparison.OrdinalIgnoreCase)
            ? AgentReasoningFormat.ReasoningObject
            : AgentReasoningFormat.ReasoningEffort;

    /// <summary>
    /// Lower-cases the value and falls back to <see cref="DefaultReasoningEffort"/>
    /// for anything the ladder and "none" do not contain. An unset/empty value is
    /// the normal state (no env var, no settings override) and defaults silently.
    /// </summary>
    public static string NormalizeReasoningEffort(string? effort)
    {
        var normalized = (effort ?? string.Empty).Trim().ToLowerInvariant();
        if (Array.IndexOf(ReasoningEffortChoices, normalized) >= 0)
            return normalized;

        if (normalized.Length == 0)
            return DefaultReasoningEffort;

        SmartCon.Core.Logging.SmartConLogger.Warn(
            "Agent reasoning effort '" + effort + "' is not one of " +
            string.Join(", ", ReasoningEffortChoices) + "; using '" + DefaultReasoningEffort +
            "' [Action: fix SMARTCON_LLM_REASONING_EFFORT or the agent settings]");
        return DefaultReasoningEffort;
    }

    public static int ClampMaxOutputTokens(int value) =>
        Math.Max(MinMaxOutputTokens, Math.Min(value, MaxMaxOutputTokens));

    /// <summary>
    /// Parses the reasoning format override of the environment / secrets.env
    /// (a property of the provider API format, not a user setting):
    /// none | reasoning_effort | reasoning | auto (null).
    /// </summary>
    public static AgentReasoningFormat? ParseReasoningFormatChoice(string? choice)
    {
        var normalized = (choice ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "":
            case "auto":
                return null;
            case "none":
                return AgentReasoningFormat.None;
            case "reasoning_effort":
                return AgentReasoningFormat.ReasoningEffort;
            case "reasoning":
                return AgentReasoningFormat.ReasoningObject;
            default:
                SmartCon.Core.Logging.SmartConLogger.Warn(
                    "Agent reasoning format '" + choice + "' is not one of " +
                    "auto, none, reasoning_effort, reasoning; using auto by provider " +
                    "[Action: fix SMARTCON_LLM_REASONING_FORMAT or the agent settings]");
                return null;
        }
    }

    public static string ReasoningFormatChoiceFor(AgentReasoningFormat? format) =>
        format switch
        {
            null => "auto",
            AgentReasoningFormat.None => "none",
            AgentReasoningFormat.ReasoningEffort => "reasoning_effort",
            AgentReasoningFormat.ReasoningObject => "reasoning",
            _ => "auto"
        };

    private static string BaseUrlEnvKey(string? provider) =>
        IsOpenCodeGoProvider(provider) ? "SMARTCON_OPENCODE_GO_BASE_URL"
        : IsZaiProvider(provider) ? "SMARTCON_ZAI_BASE_URL"
        : "SMARTCON_OPENROUTER_BASE_URL";

    private static string ApiKeyEnvKey(string? provider) =>
        IsOpenCodeGoProvider(provider) ? "SMARTCON_OPENCODE_GO_API_KEY"
        : IsZaiProvider(provider) ? "SMARTCON_ZAI_API_KEY"
        : "SMARTCON_OPENROUTER_API_KEY";

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public static AgentLlmSettings Load()
    {
        var values = LoadEnvValues();

        var provider = Get(values, "SMARTCON_LLM_PROVIDER") ?? "openrouter";

        var baseUrl = Get(values, BaseUrlEnvKey(provider)) ?? DefaultBaseUrlFor(provider);
        var apiKey = Get(values, ApiKeyEnvKey(provider)) ?? string.Empty;

        return new AgentLlmSettings
        {
            Provider = provider,
            Model = Get(values, "SMARTCON_LLM_MODEL") ?? DefaultModelFor(provider),
            BaseUrl = baseUrl.TrimEnd('/'),
            ApiKey = apiKey,
            Temperature = ParseDouble(Get(values, "SMARTCON_LLM_TEMPERATURE"), 0.2),
            MaxOutputTokens = ClampMaxOutputTokens(
                ParseInt(Get(values, "SMARTCON_LLM_MAX_OUTPUT_TOKENS"), DefaultMaxOutputTokens)),
            TimeoutSeconds = ParseInt(Get(values, "SMARTCON_LLM_TIMEOUT_SECONDS"), 120),
            MaxRequestSeconds = Math.Max(
                60,
                Math.Min(
                    ParseInt(Get(values, "SMARTCON_LLM_MAX_REQUEST_SECONDS"), DefaultMaxRequestSeconds),
                    7_200)),
            ReasoningFormat = ParseReasoningFormatChoice(
                Get(values, "SMARTCON_LLM_REASONING_FORMAT")),
            ReasoningEffort = NormalizeReasoningEffort(
                Get(values, "SMARTCON_LLM_REASONING_EFFORT")),
            ContextWindowTokens = Math.Max(
                50_000,
                Math.Min(
                    ParseInt(Get(values, "SMARTCON_AGENT_CONTEXT_TOKENS"), 500_000),
                    2_000_000)),
            ContextThreshold = Math.Max(
                0.3,
                Math.Min(
                    ParseDouble(Get(values, "SMARTCON_AGENT_CONTEXT_THRESHOLD"), 1.0),
                    1.0)),
            SummaryModel = Get(values, "SMARTCON_AGENT_SUMMARY_MODEL") ?? string.Empty,
            SummaryMaxInputTokens = Math.Max(
                4_000,
                Math.Min(
                    ParseInt(Get(values, "SMARTCON_AGENT_SUMMARY_MAX_INPUT_TOKENS"), 24_000),
                    200_000)),
            SummaryMaxOutputTokens = Math.Max(
                500,
                Math.Min(
                    ParseInt(Get(values, "SMARTCON_AGENT_SUMMARY_MAX_OUTPUT_TOKENS"), 1_500),
                    8_192))
        };
    }

    public void Apply(string provider, string model, string apiKey)
    {
        var targetProvider = string.IsNullOrWhiteSpace(provider) ? "openrouter" : provider.Trim();
        var providerChanged = !string.Equals(Provider, targetProvider, StringComparison.OrdinalIgnoreCase);

        Provider = targetProvider;
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModelFor(targetProvider) : model.Trim();

        if (providerChanged || string.IsNullOrWhiteSpace(BaseUrl))
            BaseUrl = DefaultBaseUrlFor(targetProvider);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            ApiKey = apiKey.Trim();
        }
        else if (providerChanged)
        {
            // Never carry the previous provider's key over to another provider: load the
            // key stored for the target provider, or clear it so the UI asks for one.
            ApiKey = GetStoredKeyFor(targetProvider);
        }
    }

    /// <summary>Test seam: replaces the stored-key lookup in unit tests.</summary>
    internal static Func<string, string?>? StoredKeyProvider { get; set; }

    /// <summary>
    /// Test seam: replaces the secrets.env lookup in unit tests so they never
    /// depend on a developer's real secrets.env (MEDIUM-7). Null keeps the
    /// production behavior.
    /// </summary>
    internal static Func<IReadOnlyDictionary<string, string>>? EnvValuesProvider { get; set; }

    private static IReadOnlyDictionary<string, string> LoadEnvValues() =>
        EnvValuesProvider is not null ? EnvValuesProvider() : AgentEnvFile.Load();

    /// <summary>
    /// Key stored for the given provider (environment first, then secrets.env).
    /// The settings UI uses it to show that a key already exists when the user
    /// switches the provider dropdown.
    /// </summary>
    internal static string GetStoredKeyFor(string provider)
    {
        if (StoredKeyProvider is not null)
            return StoredKeyProvider(provider) ?? string.Empty;

        var keyName = ApiKeyEnvKey(provider);

        var fromEnvironment = Environment.GetEnvironmentVariable(keyName);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment.Trim();

        var values = LoadEnvValues();
        return values.TryGetValue(keyName, out var value) ? value.Trim() : string.Empty;
    }

    public bool Save()
    {
        return AgentEnvFile.Save(BuildSaveValues());
    }

    /// <summary>The env-file entries of this settings instance (test seam).</summary>
    internal IReadOnlyDictionary<string, string> BuildSaveValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SMARTCON_LLM_PROVIDER"] = Provider,
            ["SMARTCON_LLM_MODEL"] = Model,
            ["SMARTCON_LLM_REASONING_FORMAT"] = ReasoningFormatChoiceFor(ReasoningFormat),
            ["SMARTCON_LLM_REASONING_EFFORT"] = ReasoningEffort,
            ["SMARTCON_LLM_MAX_OUTPUT_TOKENS"] = MaxOutputTokens.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
        };

        values[ApiKeyEnvKey(Provider)] = ApiKey;
        values[BaseUrlEnvKey(Provider)] = BaseUrl;
        return values;
    }

    private static double ParseDouble(string? value, double fallback) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static string? Get(IReadOnlyDictionary<string, string> values, string key)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment;

        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }
}
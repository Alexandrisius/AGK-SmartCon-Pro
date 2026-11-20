namespace SmartCon.Agent.Services.Llm;

public sealed class AgentLlmSettings
{
    public const string DefaultOpenRouterBaseUrl = "https://openrouter.ai/api/v1";
    public const string DefaultOpenCodeGoBaseUrl = "https://opencode.ai/inference/go/openai/v1";
    public const string DefaultZaiBaseUrl = "https://api.z.ai/api/coding/paas/v4";
    public const string DefaultModel = "deepseek/deepseek-v4.1-flash-1017";
    public const string DefaultOpenCodeGoModel = "deepseek-v4.1-flash";
    public const string DefaultZaiModel = "glm-5.3";

    public string Provider { get; set; } = "openrouter";

    public string Model { get; set; } = DefaultModel;

    public string BaseUrl { get; set; } = DefaultOpenRouterBaseUrl;

    public string ApiKey { get; set; } = string.Empty;

    public double Temperature { get; set; } = 0.2;

    public int MaxOutputTokens { get; set; } = 8192;

    public int TimeoutSeconds { get; set; } = 120;

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
            MaxOutputTokens = ParseInt(Get(values, "SMARTCON_LLM_MAX_OUTPUT_TOKENS"), 8192),
            TimeoutSeconds = ParseInt(Get(values, "SMARTCON_LLM_TIMEOUT_SECONDS"), 120),
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
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SMARTCON_LLM_PROVIDER"] = Provider,
            ["SMARTCON_LLM_MODEL"] = Model
        };

        values[ApiKeyEnvKey(Provider)] = ApiKey;
        values[BaseUrlEnvKey(Provider)] = BaseUrl;

        return AgentEnvFile.Save(values);
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
namespace SmartCon.Agent.Services.Llm;

public sealed class AgentLlmSettings
{
    public const string DefaultOpenRouterBaseUrl = "https://openrouter.ai/api/v1";
    public const string DefaultOpenCodeGoBaseUrl = "https://opencode.ai/inference/go/openai/v1";
    public const string DefaultModel = "deepseek/deepseek-v4.1-flash-1017";

    public string Provider { get; set; } = "openrouter";

    public string Model { get; set; } = DefaultModel;

    public string BaseUrl { get; set; } = DefaultOpenRouterBaseUrl;

    public string ApiKey { get; set; } = string.Empty;

    public string ProxyUrl { get; set; } = string.Empty;

    public double Temperature { get; set; } = 0.2;

    public int MaxOutputTokens { get; set; } = 8192;

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Context window used by the history compactor, in tokens. The model has a
    /// 1M window, but keeping the working set small keeps latency and cost low.
    /// </summary>
    public int ContextWindowTokens { get; set; } = 200_000;

    /// <summary>
    /// Send only a BM25 shortlist of tool schemas per turn (core read tools are
    /// always included; find_tools covers the rest). Disable with
    /// SMARTCON_AGENT_TOOL_ROUTER=0.
    /// </summary>
    public bool UseToolRouter { get; set; } = true;

    public bool IsOpenCodeGo =>
        string.Equals(Provider, "opencode-go", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Provider, "opencode", StringComparison.OrdinalIgnoreCase);

    public static bool IsOpenCodeGoProvider(string? provider) =>
        string.Equals(provider, "opencode-go", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "opencode", StringComparison.OrdinalIgnoreCase);

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public static AgentLlmSettings Load()
    {
        var values = AgentEnvFile.Load();

        var provider = Get(values, "SMARTCON_LLM_PROVIDER") ?? "openrouter";
        var openCodeGo = string.Equals(provider, "opencode-go", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "opencode", StringComparison.OrdinalIgnoreCase);

        var baseUrl = openCodeGo
            ? Get(values, "SMARTCON_OPENCODE_GO_BASE_URL") ?? DefaultOpenCodeGoBaseUrl
            : Get(values, "SMARTCON_OPENROUTER_BASE_URL") ?? DefaultOpenRouterBaseUrl;

        var apiKey = openCodeGo
            ? Get(values, "SMARTCON_OPENCODE_GO_API_KEY") ?? string.Empty
            : Get(values, "SMARTCON_OPENROUTER_API_KEY") ?? string.Empty;

        return new AgentLlmSettings
        {
            Provider = provider,
            Model = Get(values, "SMARTCON_LLM_MODEL") ?? DefaultModel,
            BaseUrl = baseUrl.TrimEnd('/'),
            ApiKey = apiKey,
            ProxyUrl = Get(values, "SMARTCON_LLM_PROXY") ?? string.Empty,
            Temperature = ParseDouble(Get(values, "SMARTCON_LLM_TEMPERATURE"), 0.2),
            MaxOutputTokens = ParseInt(Get(values, "SMARTCON_LLM_MAX_OUTPUT_TOKENS"), 8192),
            TimeoutSeconds = ParseInt(Get(values, "SMARTCON_LLM_TIMEOUT_SECONDS"), 120),
            UseToolRouter = !string.Equals(
                Get(values, "SMARTCON_AGENT_TOOL_ROUTER"), "0", StringComparison.Ordinal)
        };
    }

    public void Apply(string provider, string model, string apiKey, string proxyUrl)
    {
        var targetProvider = string.IsNullOrWhiteSpace(provider) ? "openrouter" : provider.Trim();
        var providerChanged = !string.Equals(Provider, targetProvider, StringComparison.OrdinalIgnoreCase);

        Provider = targetProvider;
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        BaseUrl = IsOpenCodeGo ? DefaultOpenCodeGoBaseUrl : DefaultOpenRouterBaseUrl;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            ApiKey = apiKey.Trim();
        }
        else if (providerChanged)
        {
            // Never carry the previous provider's key over to another provider: load the
            // key stored for the target provider, or clear it so the UI asks for one.
            ApiKey = LoadStoredKey(targetProvider);
        }

        ProxyUrl = proxyUrl?.Trim() ?? string.Empty;
    }

    /// <summary>Test seam: replaces the stored-key lookup in unit tests.</summary>
    internal static Func<string, string?>? StoredKeyProvider { get; set; }

    private static string LoadStoredKey(string provider)
    {
        if (StoredKeyProvider is not null)
            return StoredKeyProvider(provider) ?? string.Empty;

        var keyName = IsOpenCodeGoProvider(provider)
            ? "SMARTCON_OPENCODE_GO_API_KEY"
            : "SMARTCON_OPENROUTER_API_KEY";

        var fromEnvironment = Environment.GetEnvironmentVariable(keyName);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment.Trim();

        var values = AgentEnvFile.Load();
        return values.TryGetValue(keyName, out var value) ? value.Trim() : string.Empty;
    }

    public void Save()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SMARTCON_LLM_PROVIDER"] = Provider,
            ["SMARTCON_LLM_MODEL"] = Model,
            ["SMARTCON_LLM_PROXY"] = ProxyUrl
        };

        if (IsOpenCodeGo)
        {
            values["SMARTCON_OPENCODE_GO_API_KEY"] = ApiKey;
            values["SMARTCON_OPENCODE_GO_BASE_URL"] = BaseUrl;
        }
        else
        {
            values["SMARTCON_OPENROUTER_API_KEY"] = ApiKey;
            values["SMARTCON_OPENROUTER_BASE_URL"] = BaseUrl;
        }

        AgentEnvFile.Save(values);
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
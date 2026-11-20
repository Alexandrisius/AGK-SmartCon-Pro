using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Logging;

namespace SmartCon.Agent.Services.Decisions;

/// <summary>
/// Configuration of the optional decision layer (System One models: JEV,
/// pplx-decider). The chat provider and the decision provider are independent:
/// OpenRouter Decisions API needs an OpenRouter key even when the chat model
/// runs on OpenCode Go, so the decision key has its own field and its own
/// secrets.env entry (<c>SMARTCON_DECIDER_API_KEY</c>). Disabled unless a model
/// is chosen in the settings UI or <c>SMARTCON_DECIDER_ENABLED=1</c> is set.
/// </summary>
public sealed class AgentDecisionsSettings
{
    public const string DefaultOpenRouterModel = "perplexity/pplx-decider-v1.3:free";
    public const string DefaultTypeSafeModel = "jev-1.13.0";
    public const string DefaultPerplexityModel = "pplx-decider-latest";
    public const string DefaultOpenRouterEndpoint = "https://openrouter.ai/api/alpha/decisions";
    public const string DefaultTypeSafeEndpoint = "https://api.typesafe.ai/v1/systemone";
    public const string DefaultPerplexityEndpoint = "https://api.perplexity.ai/v1/decisions";

    public static readonly IReadOnlyList<string> Providers =
        new[] { "openrouter", "perplexity", "typesafe" };

    public bool Enabled { get; private set; }

    public string Provider { get; private set; } = "openrouter";

    public string Model { get; private set; } = DefaultOpenRouterModel;

    public string Endpoint { get; private set; } = DefaultOpenRouterEndpoint;

    public string ApiKey { get; private set; } = string.Empty;

    public int TimeoutMilliseconds { get; init; } = 5000;

    /// <summary>Use the decision model as the primary tool router (JEV/pplx Choice).</summary>
    public bool UseRouter { get; init; }

    public bool IsAvailable => Enabled && !string.IsNullOrWhiteSpace(ApiKey);

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public static string NormalizeProvider(string? provider)
    {
        var value = provider?.Trim().ToLowerInvariant() ?? string.Empty;
        switch (value)
        {
            case "typesafe":
            case "jev":
                return "typesafe";
            case "perplexity":
            case "pplx":
                return "perplexity";
            default:
                return "openrouter";
        }
    }

    public static string DefaultModelFor(string provider)
    {
        switch (NormalizeProvider(provider))
        {
            case "typesafe":
                return DefaultTypeSafeModel;
            case "perplexity":
                return DefaultPerplexityModel;
            default:
                return DefaultOpenRouterModel;
        }
    }

    public static string DefaultEndpointFor(string provider)
    {
        switch (NormalizeProvider(provider))
        {
            case "typesafe":
                return DefaultTypeSafeEndpoint;
            case "perplexity":
                return DefaultPerplexityEndpoint;
            default:
                return DefaultOpenRouterEndpoint;
        }
    }

    /// <summary>
    /// Key that the decision layer would use for the given provider: the
    /// dedicated decider key first, then the provider's legacy key name. The
    /// settings UI calls it to show dots when a key already exists.
    /// </summary>
    public static string GetStoredKeyFor(string provider) =>
        StoredKeyFrom(AgentEnvFile.Load(), provider);

    private static string StoredKeyFrom(
        IReadOnlyDictionary<string, string> values,
        string provider)
    {
        var dedicated = Get(values, "SMARTCON_DECIDER_API_KEY");
        if (!string.IsNullOrWhiteSpace(dedicated))
            return dedicated!.Trim();

        switch (NormalizeProvider(provider))
        {
            case "typesafe":
                return (Get(values, "SMARTCON_JEV_API_KEY")
                        ?? Get(values, "TYPESAFE_API_KEY"))?.Trim() ?? string.Empty;

            case "perplexity":
                return (Get(values, "SMARTCON_PPLX_API_KEY")
                        ?? Get(values, "PERPLEXITY_API_KEY"))?.Trim() ?? string.Empty;

            default:
                return Get(values, "SMARTCON_OPENROUTER_API_KEY")?.Trim() ?? string.Empty;
        }
    }

    /// <summary>
    /// Applies the decision provider, model and key chosen in the settings UI
    /// and persists them to secrets.env. An empty model disables the decision
    /// layer without losing the last model; an empty key keeps the current one
    /// (the UI passes the stored key back, so it is never taken from nowhere).
    /// </summary>
    public void Apply(string? provider, string? model, string? apiKey)
    {
        var target = NormalizeProvider(provider);
        var providerChanged = !string.Equals(Provider, target, StringComparison.Ordinal);

        Provider = target;
        if (providerChanged || string.IsNullOrWhiteSpace(Endpoint))
            Endpoint = DefaultEndpointFor(target);

        var modelValue = model?.Trim() ?? string.Empty;
        Enabled = modelValue.Length > 0;
        if (modelValue.Length > 0)
            Model = modelValue;

        if (!string.IsNullOrWhiteSpace(apiKey))
            ApiKey = apiKey!.Trim();
        else if (providerChanged)
            ApiKey = GetStoredKeyFor(target);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SMARTCON_DECIDER_ENABLED"] = Enabled ? "1" : "0",
            ["SMARTCON_DECIDER_PROVIDER"] = Provider,
            ["SMARTCON_DECIDER_MODEL"] = Model,
            ["SMARTCON_DECIDER_BASE_URL"] = Endpoint
        };

        if (!string.IsNullOrWhiteSpace(ApiKey))
            values["SMARTCON_DECIDER_API_KEY"] = ApiKey;

        if (!AgentEnvFile.Save(values))
        {
            SmartConLogger.Warn(
                "Decision settings were not persisted to secrets.env " +
                "[Action: check write access to %APPDATA%\\AGK\\SmartCon\\Agent]");
        }
    }

    public static AgentDecisionsSettings Load()
    {
        var values = AgentEnvFile.Load();

        var provider = NormalizeProvider(Get(values, "SMARTCON_DECIDER_PROVIDER"));
        var endpoint = Get(values, "SMARTCON_DECIDER_BASE_URL")
                       ?? DefaultEndpointFor(provider);

        var timeout = 5000;
        var timeoutText = Get(values, "SMARTCON_DECIDER_TIMEOUT_MS");
        if (int.TryParse(timeoutText, out var parsed) && parsed >= 500 && parsed <= 30000)
            timeout = parsed;

        return new AgentDecisionsSettings
        {
            Enabled = string.Equals(
                Get(values, "SMARTCON_DECIDER_ENABLED"), "1", StringComparison.Ordinal),
            Provider = provider,
            Model = Get(values, "SMARTCON_DECIDER_MODEL") ?? DefaultModelFor(provider),
            Endpoint = endpoint,
            ApiKey = StoredKeyFrom(values, provider),
            TimeoutMilliseconds = timeout,
            UseRouter = string.Equals(
                Get(values, "SMARTCON_DECIDER_ROUTER"), "1", StringComparison.Ordinal)
        };
    }

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
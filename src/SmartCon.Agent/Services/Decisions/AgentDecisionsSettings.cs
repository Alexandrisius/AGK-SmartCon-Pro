using SmartCon.Agent.Services.Llm;

namespace SmartCon.Agent.Services.Decisions;

/// <summary>
/// Configuration of the optional decision layer (System One models: JEV,
/// pplx-decider). Defaults to the free Perplexity decider served by the
/// OpenRouter Decisions API (no VPN needed, same key as the chat model).
/// Disabled unless SMARTCON_DECIDER_ENABLED=1.
/// </summary>
public sealed class AgentDecisionsSettings
{
    public const string DefaultOpenRouterModel = "perplexity/pplx-decider-v1.3:free";
    public const string DefaultTypeSafeModel = "jev-1.13.0";
    public const string DefaultPerplexityModel = "pplx-decider-latest";
    public const string DefaultOpenRouterEndpoint = "https://openrouter.ai/api/alpha/decisions";
    public const string DefaultTypeSafeEndpoint = "https://api.typesafe.ai/v1/systemone";
    public const string DefaultPerplexityEndpoint = "https://api.perplexity.ai/v1/decisions";

    public bool Enabled { get; private set; }

    public string Provider { get; init; } = "openrouter";

    public string Model { get; private set; } = DefaultOpenRouterModel;

    public required string Endpoint { get; init; }

    public string ApiKey { get; init; } = string.Empty;

    public int TimeoutMilliseconds { get; init; } = 5000;

    /// <summary>Use the decision model as the primary tool router (JEV/pplx Choice).</summary>
    public bool UseRouter { get; init; }

    public bool IsAvailable => Enabled && !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Applies the model chosen in the settings UI and persists it to
    /// secrets.env. An empty value disables the decision layer without losing
    /// the last model.
    /// </summary>
    public void Apply(string? model)
    {
        var value = model?.Trim() ?? string.Empty;
        Enabled = value.Length > 0;
        if (value.Length > 0)
            Model = value;

        AgentEnvFile.Save(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SMARTCON_DECIDER_ENABLED"] = Enabled ? "1" : "0",
            ["SMARTCON_DECIDER_MODEL"] = Model
        });
    }

    public static AgentDecisionsSettings Load()
    {
        var values = AgentEnvFile.Load();

        var provider = (Get(values, "SMARTCON_DECIDER_PROVIDER") ?? "openrouter")
            .Trim()
            .ToLowerInvariant();

        string endpoint;
        string model;
        string? key;
        switch (provider)
        {
            case "typesafe":
            case "jev":
                endpoint = Get(values, "SMARTCON_DECIDER_BASE_URL") ?? DefaultTypeSafeEndpoint;
                model = Get(values, "SMARTCON_DECIDER_MODEL") ?? DefaultTypeSafeModel;
                key = Get(values, "SMARTCON_DECIDER_API_KEY")
                      ?? Get(values, "SMARTCON_JEV_API_KEY")
                      ?? Get(values, "TYPESAFE_API_KEY");
                break;

            case "perplexity":
            case "pplx":
                endpoint = Get(values, "SMARTCON_DECIDER_BASE_URL") ?? DefaultPerplexityEndpoint;
                model = Get(values, "SMARTCON_DECIDER_MODEL") ?? DefaultPerplexityModel;
                key = Get(values, "SMARTCON_DECIDER_API_KEY")
                      ?? Get(values, "SMARTCON_PPLX_API_KEY")
                      ?? Get(values, "PERPLEXITY_API_KEY");
                break;

            default:
                endpoint = Get(values, "SMARTCON_DECIDER_BASE_URL") ?? DefaultOpenRouterEndpoint;
                model = Get(values, "SMARTCON_DECIDER_MODEL") ?? DefaultOpenRouterModel;
                key = Get(values, "SMARTCON_DECIDER_API_KEY")
                      ?? Get(values, "SMARTCON_OPENROUTER_API_KEY");
                break;
        }

        var timeout = 5000;
        var timeoutText = Get(values, "SMARTCON_DECIDER_TIMEOUT_MS");
        if (int.TryParse(timeoutText, out var parsed) && parsed >= 500 && parsed <= 30000)
            timeout = parsed;

        return new AgentDecisionsSettings
        {
            Enabled = string.Equals(
                Get(values, "SMARTCON_DECIDER_ENABLED"), "1", StringComparison.Ordinal),
            Provider = provider,
            Model = model!,
            Endpoint = endpoint!,
            ApiKey = key ?? string.Empty,
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

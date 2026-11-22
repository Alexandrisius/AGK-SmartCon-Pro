using System.Globalization;

namespace SmartCon.Agent.Services.Llm;

public enum AgentCacheKind
{
    /// <summary>Unknown provider: send nothing, never guess.</summary>
    None = 0,

    /// <summary>Provider caches prefixes automatically (DeepSeek, Gemini).</summary>
    Implicit = 1,

    /// <summary>OpenAI-style explicit routing key.</summary>
    PromptCacheKey = 2,

    /// <summary>Anthropic-style <c>cache_control</c> breakpoints.</summary>
    EphemeralBreakpoints = 3
}

/// <summary>
/// Cache directives of one request (P.5). Resolved from the provider and model,
/// applied by <see cref="LlmPayloadBuilder"/> and the HTTP client.
/// </summary>
public sealed class AgentCachePlan
{
    public required AgentCacheKind Kind { get; init; }

    /// <summary>Provider|model|effort: a change means a different cache.</summary>
    public required string Namespace { get; init; }

    /// <summary>Value of the per-request <c>x-opencode-session</c> header.</summary>
    public string? OpenCodeSession { get; init; }

    /// <summary>OpenAI-style top-level <c>prompt_cache_key</c>.</summary>
    public string? PromptCacheKey { get; init; }

    /// <summary>Mark the stable system blocks with <c>cache_control</c>.</summary>
    public bool UseAnthropicBreakpoints { get; init; }

    /// <summary>Extra top-level request fields (OpenRouter <c>session_id</c>).</summary>
    public IReadOnlyDictionary<string, object?> ProviderOptions { get; init; } =
        new Dictionary<string, object?>();

    public string KindName =>
        Kind switch
        {
            AgentCacheKind.PromptCacheKey => "prompt_cache_key",
            AgentCacheKind.EphemeralBreakpoints => "ephemeral_breakpoints",
            AgentCacheKind.Implicit => "implicit",
            _ => "none"
        };
}

/// <summary>
/// Provider cache policy (P.5), grounded in the 2026 provider documentation:
/// <list type="bullet">
/// <item>OpenAI: top-level <c>prompt_cache_key</c> for cache routing on models before GPT-5.6.</item>
/// <item>Anthropic: explicit <c>cache_control</c> breakpoints on the stable system blocks
/// (tools render before system, so the first system block covers both), at most 4.</item>
/// <item>DeepSeek, Gemini and Z.ai GLM: implicit caching, no request fields at all.</item>
/// <item>OpenRouter: <c>session_id</c> for sticky routing; Anthropic markers pass through.</item>
/// <item>OpenCode Go: per-conversation <c>x-opencode-session</c> header.</item>
/// </list>
/// An unknown provider gets no cache fields and a warning in the client.
/// </summary>
public static class AgentProviderCachePolicy
{
    public const int MaxCacheKeyLength = 64;
    public const int MaxSessionIdLength = 256;

    public static AgentCachePlan Resolve(
        string? provider,
        string? model,
        double? temperature,
        Guid? conversationId,
        string? cacheKey = null,
        string? reasoningEffort = null,
        AgentReasoningFormat reasoningFormat = AgentReasoningFormat.None)
    {
        var normalizedProvider = provider?.Trim().ToLowerInvariant() ?? string.Empty;
        var normalizedModel = model?.Trim() ?? string.Empty;
        var key = BuildCacheKey(cacheKey, conversationId);
        var family = FamilyOf(normalizedProvider);
        var kind = KindFor(family, normalizedModel);
        var options = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (family == ProviderFamily.OpenRouter && key.Length > 0)
            options["session_id"] = "ses_" + key;

        return new AgentCachePlan
        {
            Kind = kind,
            Namespace = BuildNamespace(
                normalizedProvider, normalizedModel, temperature,
                reasoningEffort, reasoningFormat),
            OpenCodeSession = family == ProviderFamily.OpenCodeGo && key.Length > 0
                ? "ses_" + key
                : null,
            PromptCacheKey = kind == AgentCacheKind.PromptCacheKey && key.Length > 0
                ? Truncate(key, MaxCacheKeyLength)
                : null,
            UseAnthropicBreakpoints = kind == AgentCacheKind.EphemeralBreakpoints,
            ProviderOptions = options
        };
    }

    /// <summary>
    /// Provider|model|temperature tuple plus the effective reasoning effort and
    /// format when a reasoning parameter is sent; a change means a different cache.
    /// </summary>
    public static string BuildNamespace(
        string? provider,
        string? model,
        double? temperature,
        string? reasoningEffort = null,
        AgentReasoningFormat reasoningFormat = AgentReasoningFormat.None)
    {
        var value = (provider ?? string.Empty).Trim().ToLowerInvariant()
            + "|" + (model ?? string.Empty).Trim()
            + "|t=" + (temperature?.ToString("F2", CultureInfo.InvariantCulture) ?? "-");

        if (reasoningFormat != AgentReasoningFormat.None)
        {
            value += "|e=" + (reasoningEffort ?? "-")
                + "|rf=" + FormatToken(reasoningFormat);
        }

        return value;
    }

    private static string FormatToken(AgentReasoningFormat format) => format switch
    {
        AgentReasoningFormat.ReasoningEffort => "reasoning_effort",
        AgentReasoningFormat.ReasoningObject => "reasoning",
        _ => "none"
    };

    /// <summary>
    /// Log line when the cache namespace of the client changes (model or effort
    /// switch); null when the namespace is unchanged or unknown yet.
    /// </summary>
    public static string? DescribeNamespaceChange(string? previous, string next) =>
        previous is not null && !string.Equals(previous, next, StringComparison.Ordinal)
            ? "Agent cache namespace changed: " + previous + " -> " + next
            : null;

    /// <summary>
    /// Stable cache key of a conversation: the explicit key when the caller set
    /// one, otherwise the conversation id without dashes.
    /// </summary>
    public static string BuildCacheKey(string? cacheKey, Guid? conversationId)
    {
        var value = !string.IsNullOrWhiteSpace(cacheKey)
            ? cacheKey!.Trim()
            : conversationId.HasValue
                ? conversationId.Value.ToString("N")
                : string.Empty;

        return value.Length <= MaxSessionIdLength
            ? value
            : value.Substring(0, MaxSessionIdLength);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value.Substring(0, maxLength);

    private static AgentCacheKind KindFor(ProviderFamily family, string model)
    {
        switch (family)
        {
            case ProviderFamily.OpenRouter:
                return ModelFamily(model) switch
                {
                    AgentModelFamily.Anthropic => AgentCacheKind.EphemeralBreakpoints,
                    AgentModelFamily.DeepSeek or AgentModelFamily.Gemini => AgentCacheKind.Implicit,
                    _ => AgentCacheKind.PromptCacheKey
                };

            case ProviderFamily.OpenCodeGo:
                // The documented OpenCode Go mechanism is the session header; do not
                // guess body fields for models it proxies.
                return ModelFamily(model) == AgentModelFamily.Anthropic
                    ? AgentCacheKind.EphemeralBreakpoints
                    : AgentCacheKind.Implicit;

            case ProviderFamily.OpenAi:
                return AgentCacheKind.PromptCacheKey;

            case ProviderFamily.Anthropic:
                return AgentCacheKind.EphemeralBreakpoints;

            case ProviderFamily.DeepSeek:
            case ProviderFamily.Gemini:
            case ProviderFamily.Zai:
                return AgentCacheKind.Implicit;

            default:
                return AgentCacheKind.None;
        }
    }

    private static ProviderFamily FamilyOf(string provider) =>
        provider switch
        {
            "openrouter" => ProviderFamily.OpenRouter,
            "opencode-go" or "opencode" => ProviderFamily.OpenCodeGo,
            "openai" => ProviderFamily.OpenAi,
            "anthropic" => ProviderFamily.Anthropic,
            "deepseek" => ProviderFamily.DeepSeek,
            "gemini" or "google" => ProviderFamily.Gemini,
            "zai-glm" or "zai" => ProviderFamily.Zai,
            _ => ProviderFamily.Unknown
        };

    private static AgentModelFamily ModelFamily(string model)
    {
        if (model.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase)
            || model.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return AgentModelFamily.Anthropic;
        }

        if (model.IndexOf("deepseek", StringComparison.OrdinalIgnoreCase) >= 0)
            return AgentModelFamily.DeepSeek;

        if (model.StartsWith("google/", StringComparison.OrdinalIgnoreCase)
            || model.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return AgentModelFamily.Gemini;
        }

        if (model.StartsWith("openai/", StringComparison.OrdinalIgnoreCase)
            || model.IndexOf("gpt-", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return AgentModelFamily.OpenAi;
        }

        return AgentModelFamily.Other;
    }

    private enum ProviderFamily
    {
        Unknown = 0,
        OpenRouter = 1,
        OpenCodeGo = 2,
        OpenAi = 3,
        Anthropic = 4,
        DeepSeek = 5,
        Gemini = 6,
        Zai = 7
    }

    private enum AgentModelFamily
    {
        Other = 0,
        OpenAi = 1,
        Anthropic = 2,
        DeepSeek = 3,
        Gemini = 4
    }
}
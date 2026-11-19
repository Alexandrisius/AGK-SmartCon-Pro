using System.Globalization;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services.Llm;

/// <summary>
/// One line per LLM response that makes the provider prompt cache observable:
/// cache read/write, cost and the hashes of the two cache-relevant prompt parts.
/// </summary>
public static class AgentCacheLog
{
    public const string Prefix = "Agent cache:";

    public static string Build(
        int round,
        string? requestId,
        AgentUsage usage,
        string staticHash,
        string toolSetHash)
    {
        if (usage is null) throw new ArgumentNullException(nameof(usage));

        return Prefix +
               " round=" + round.ToString(CultureInfo.InvariantCulture) +
               " request_id=" + (string.IsNullOrEmpty(requestId) ? "(none)" : requestId) +
               " prompt_tokens=" + usage.PromptTokens.ToString(CultureInfo.InvariantCulture) +
               " cached_tokens=" + usage.CachedTokens.ToString(CultureInfo.InvariantCulture) +
               " cache_write_tokens=" + usage.CacheWriteTokens.ToString(CultureInfo.InvariantCulture) +
               " cost_usd=" + FormatCost(usage.CostUsd) +
               " static_hash=" + staticHash +
               " tool_set_hash=" + toolSetHash;
    }

    private static string FormatCost(decimal? cost) =>
        cost.HasValue
            ? cost.Value.ToString("F6", CultureInfo.InvariantCulture)
            : "(none)";
}
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Agent.Services;

public sealed partial class AgentOrchestrator
{
    /// <summary>
    /// Runs one call and turns what the guards observed into warnings on its
    /// result: the loop-guard hint and the call-storm hints. A hint is only
    /// ever attached to a tool result: a message between a tool call and its
    /// result is not a valid conversation. Nothing here ends the turn — a host
    /// that stopped answering is reported to the model, which tells the user;
    /// only Stop and the loop guard end a turn.
    /// </summary>
    private async Task<ToolResult> ExecuteObservedToolAsync(
        LoopGuard loopGuard,
        ToolStormTracker storms,
        AgentToolCall toolCall,
        string argumentsJson,
        bool exempt,
        int round,
        CancellationToken ct)
    {
        var result = await ExecuteToolAsync(toolCall, ct).ConfigureAwait(false);

        var observed = loopGuard.ObserveResult(toolCall.Name, argumentsJson, result.Success, exempt);
        if (observed.WarnOnly && !result.Success)
        {
            SmartConLogger.Info(
                "Agent loop guard warning: tool=" + toolCall.Name +
                ", identicalErrors=" + observed.IdenticalErrors);
            if (!string.IsNullOrEmpty(observed.Hint))
                result = result.WithWarnings(new[] { observed.Hint! });
        }

        var hints = storms.Observe(_toolRegistry, toolCall.Name, argumentsJson, result, round);
        if (hints.Count > 0)
        {
            SmartConLogger.Info(
                "Agent call-storm hint: tool=" + toolCall.Name + ", round=" + round +
                ", hints=" + hints.Count);
            result = result.WithWarnings(hints);
        }

        return result;
    }

    /// <summary>
    /// The conversation-scoped paging guard (S.45): survives user messages,
    /// entries decay after ten minutes without a call.
    /// </summary>
    private PaginationGuard PaginationGuardFor(Guid conversationId)
    {
        if (_paginationGuards.Count > 8)
            _paginationGuards.Clear();

        if (!_paginationGuards.TryGetValue(conversationId, out var guard))
            _paginationGuards[conversationId] = guard = new PaginationGuard();

        return guard;
    }

    private bool IsReadTool(string toolName) =>
        _toolRegistry.TryGet(toolName, out var tool)
        && tool is not null
        && tool.Definition.Risk == AgentToolRisk.Read;

    private sealed class CachedToolResult
    {
        public ToolResult Result = null!;

        public string Handle = string.Empty;

        /// <summary>How many times this cached result was served again.</summary>
        public int Served;
    }

    /// <summary>
    /// A successful read is remembered for the rest of the turn: the first
    /// identical repeat is served from the cache (no second Revit execution),
    /// the next one is refused with duplicate_call (S.45). Work that is still
    /// in progress must stay pollable, so mustVerify results are not cached.
    /// Any successful write clears the cache — the stored payload describes
    /// the previous model state.
    /// </summary>
    private static bool TryServeCachedResult(
        Dictionary<string, CachedToolResult> cache,
        AgentToolCall toolCall,
        string argumentsJson,
        out CachedToolResult cached)
    {
        cached = null!;
        if (cache.Count == 0)
            return false;

        var key = ToolCallIdentity.Canonical(toolCall.Name, argumentsJson);
        return key is not null && cache.TryGetValue(key, out cached!) && cached is not null;
    }

    private void TrackResultCache(
        Dictionary<string, CachedToolResult> cache,
        AgentToolCall toolCall,
        ToolResult result,
        string? handle)
    {
        if (result.Success && !IsReadTool(toolCall.Name))
        {
            cache.Clear();
            return;
        }

        if (!result.Success
            || !IsReadTool(toolCall.Name)
            || IsInProgressResult(result))
        {
            return;
        }

        // A result that carries a pack manual (load_tool_pack, first write) is a
        // one-shot handoff: repeating it must re-answer with skillAlreadySent,
        // not replay the manual.
        if (result.Skills.Count > 0 || CarriesSkillPayload(result))
            return;

        var key = ToolCallIdentity.Canonical(toolCall.Name, toolCall.ArgumentsJson);
        if (key is null)
            return;

        // An existing entry keeps its repeat counter: re-tracking the served
        // result must not reset how many times it was already handed out.
        if (cache.ContainsKey(key))
            return;

        if (cache.Count >= 8)
            cache.Clear();

        cache[key] = new CachedToolResult
        {
            Result = result,
            Handle = handle ?? string.Empty
        };
    }

    private static bool IsInProgressResult(ToolResult result) =>
        result.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } data
        && data.TryGetProperty("mustVerify", out var flag)
        && flag.ValueKind == System.Text.Json.JsonValueKind.True;

    private static bool CarriesSkillPayload(ToolResult result) =>
        result.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } data
        && data.TryGetProperty("skills", out var skills)
        && skills.ValueKind == System.Text.Json.JsonValueKind.Array;
}

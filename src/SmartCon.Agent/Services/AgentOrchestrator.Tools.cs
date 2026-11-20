using System.Diagnostics;
using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Agent.Services;

public sealed partial class AgentOrchestrator
{
    private const string DefaultLoopBlockedHint =
        "This exact call was already made several times. Do not repeat it; change the " +
        "arguments or ask the user what to do differently.";

    private static ToolResult LoopAbortedResult(string toolName) =>
        ToolResult.Fail(
            "loop_aborted",
            "The turn was stopped because '" + toolName + "' repeated the same call too many " +
            "times. Ask the user how to proceed with a different approach.",
            data: JsonSerializer.SerializeToElement(new { loopAborted = true, tool = toolName }));

    private async Task<ToolResult> ExecuteToolAsync(AgentToolCall toolCall, CancellationToken ct)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson);
        }
        catch (JsonException ex)
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "Arguments for tool '" + toolCall.Name + "' are not valid JSON: " + ex.Message);
        }

        using (document)
        {
            try
            {
                return await _toolExecutor
                    .ExecuteAsync(toolCall.Name, document.RootElement, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    "Agent tool '" + toolCall.Name + "' threw: " +
                    ex.GetType().Name + ": " + ex.Message +
                    " [Action: the model will receive a tool error and can retry with different arguments]");
                return ToolResult.Fail(
                    "tool_exception",
                    ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    private static string SerializeToolResult(ToolResult result)
    {
        var json = ToolResultBudget.Serialize(result);
        return AgentTextSanitizer.SanitizeJson(json);
    }

    /// <summary>
    /// Redacts and trims the tool result for the model. A result that exceeds the
    /// character budget is stored in full first, and the trimmed envelope carries
    /// its handle so <c>read_tool_result</c> can page through the rest later.
    /// </summary>
    private async Task<string> SerializeToolResultAsync(
        AgentToolCall toolCall,
        ToolResult result,
        CancellationToken ct)
    {
        var fullJson = AgentTextSanitizer.SanitizeJson(ToolResultBudget.SerializeFull(result));
        string? handle = null;

        if (fullJson.Length > ToolResultBudget.DefaultMaxCharacters
            && _toolResultBlobStore is not null
            && !string.IsNullOrWhiteSpace(toolCall.Id))
        {
            try
            {
                await _toolResultBlobStore
                    .SaveAsync(_sessionContext.ConversationId, toolCall.Id, fullJson, ct)
                    .ConfigureAwait(false);
                handle = toolCall.Id;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    "Agent failed to store the full result of '" + toolCall.Name + "': " +
                    ex.GetType().Name + ": " + ex.Message +
                    " [Action: the model receives the trimmed preview without a handle]; " +
                    "read_tool_result will not find this handle.");
            }
        }

        // Sanitize first, trim second: the sanitizer reserializes with the default
        // encoder and can inflate Cyrillic payloads past the budget.
        return fullJson.Length <= ToolResultBudget.DefaultMaxCharacters
            ? fullJson
            : ToolResultBudget.Trim(fullJson, ToolResultBudget.DefaultMaxCharacters, handle);
    }

    /// <summary>
    /// OpenAI-compatible providers reject a history where an assistant
    /// tool call has no matching tool result. A cancelled turn can leave
    /// such a gap, so missing results are synthesized as "cancelled"
    /// and orphan tool messages are dropped before the next request.
    /// </summary>
    private static List<AgentMessage> EnsureToolCallPairs(IReadOnlyList<AgentMessage> messages)
    {
        var result = new List<AgentMessage>(messages.Count);
        var pending = new List<string>();

        foreach (var message in messages)
        {
            if (message.Role == AgentRole.Tool)
            {
                if (message.ToolCallId is null || !pending.Contains(message.ToolCallId))
                    continue;

                pending.Remove(message.ToolCallId);
                result.Add(message);
                continue;
            }

            AppendMissingToolResults(result, pending);
            pending.Clear();

            if (message.Role == AgentRole.Assistant && message.ToolCalls.Count > 0)
            {
                foreach (var call in message.ToolCalls)
                    pending.Add(call.Id);
            }

            result.Add(message);
        }

        AppendMissingToolResults(result, pending);
        return result;
    }

    private static void AppendMissingToolResults(List<AgentMessage> target, List<string> pending)
    {
        foreach (var id in pending)
        {
            target.Add(AgentMessage.ToolResult(
                id,
                SerializeToolResult(ToolResult.Fail(
                    "cancelled",
                    "The tool call was not executed because the previous turn was interrupted."))));
        }
    }

    private void SetActiveToolNames(IReadOnlyList<AgentToolDefinition> definitions)
    {
        // Exactly the tools in the current request: find_tools reports them as
        // alreadyAvailable and load_tool_pack uses the same set to decide what
        // still has to be queued. Listing loaded-but-not-sent tools here is what
        // made load_tool_pack answer "already loaded" for tools the model could
        // not call at all.
        _sessionContext.ActiveToolNames =
            definitions.Select(definition => definition.Name).ToList();
    }

    private static int ElapsedMinutes(long startedAt) =>
        (int)((Stopwatch.GetTimestamp() - startedAt) * 60.0 / Stopwatch.Frequency / 60.0);

    /// <summary>
    /// Applies the tools queued by load_tool_pack at the round boundary: they
    /// join the frozen set and every following request carries them until the
    /// conversation ends. Returns the rebuilt, name-sorted definitions.
    /// </summary>
    private IReadOnlyList<AgentToolDefinition> ApplyPendingToolSet()
    {
        foreach (var name in _sessionContext.PendingToolNames)
        {
            _sessionContext.TouchTool(name);
            _sessionContext.FreezeTool(name);
        }

        _sessionContext.PendingToolNames.Clear();

        var definitions = BuildFrozenDefinitions(_toolRegistry.GetDefinitions());
        SetActiveToolNames(definitions);
        return definitions;
    }

    /// <summary>
    /// Definitions of the current request: the frozen conversation set in
    /// ordinal name order. The set is chosen once, on the first turn, from the
    /// always-available tools, the initial shortlist for the first user text and
    /// everything already loaded.
    /// </summary>
    private async Task<IReadOnlyList<AgentToolDefinition>> ResolveToolDefinitionsAsync(
        string userText,
        CancellationToken ct)
    {
        var all = _toolRegistry.GetDefinitions();
        if (all.Count == 0)
            return all;

        if (!_sessionContext.ToolSetFrozen)
        {
            var initial = await SelectInitialToolNamesAsync(userText, all, ct)
                .ConfigureAwait(false);
            foreach (var name in initial)
                _sessionContext.FreezeTool(name);
            _sessionContext.ToolSetFrozen = true;

            SmartConLogger.Info(
                "Agent tool set frozen for the conversation: " +
                _sessionContext.FrozenToolNames.Count + "/" + all.Count + " tools");
        }

        var definitions = BuildFrozenDefinitions(all);
        SmartConLogger.Debug(
            "Agent v2 tool catalog: " + definitions.Count + "/" + all.Count +
            " frozen tools, loaded=" + _sessionContext.LoadedToolNames.Count +
            ", dynamic=" + (_dynamicToolRanker is { IsEnabled: true } ? "decision" : "bm25"));
        return definitions;
    }

    /// <summary>
    /// Initial tool set of a conversation: pinned core + load_tool_pack /
    /// read_tool_result, the best matches for the first user text and every tool
    /// the session had already loaded. Later turns never re-rank: changing the
    /// list mid-conversation invalidates the provider cache (P.4).
    /// </summary>
    private async Task<IReadOnlyList<string>> SelectInitialToolNamesAsync(
        string userText,
        IReadOnlyList<AgentToolDefinition> all,
        CancellationToken ct)
    {
        const int topCount = 5;

        var selected = new List<string>(all.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name)
        {
            if (!string.IsNullOrEmpty(name) && names.Add(name))
                selected.Add(name);
        }

        foreach (var definition in all)
        {
            if (IsAlwaysAvailableV2(definition.Name))
                Add(definition.Name);
        }

        // A small catalog needs no retrieval: hiding tools from it would only cost
        // the model extra discovery rounds.
        if (all.Count <= 8)
        {
            foreach (var definition in all)
                Add(definition.Name);
        }
        else
        {
            var rankable = all
                .Where(definition => !names.Contains(definition.Name))
                .ToList();
            var dynamicEnabled = _dynamicToolRanker is { IsEnabled: true };
            var ranked = dynamicEnabled
                ? await _dynamicToolRanker!
                    .RankAsync(userText, rankable, topCount, ct)
                    .ConfigureAwait(false)
                : ToolCardRouter.Rank(userText, rankable, topCount);

            foreach (var definition in ranked)
                Add(definition.Name);
        }

        foreach (var name in _sessionContext.LoadedToolNames)
            Add(name);
        foreach (var name in _sessionContext.PendingToolNames)
            Add(name);

        return selected;
    }

    /// <summary>
    /// Frozen names resolved against the current registry and sorted by name, so
    /// two requests with the same set serialize to the same tool block.
    /// </summary>
    private IReadOnlyList<AgentToolDefinition> BuildFrozenDefinitions(
        IReadOnlyList<AgentToolDefinition> all)
    {
        if (!_sessionContext.ToolSetFrozen)
            return all;

        var byName = new Dictionary<string, AgentToolDefinition>(StringComparer.Ordinal);
        foreach (var definition in all)
            byName[definition.Name] = definition;

        var definitions = new List<AgentToolDefinition>(_sessionContext.FrozenToolNames.Count);
        foreach (var name in _sessionContext.FrozenToolNames
                     .OrderBy(candidate => candidate, StringComparer.Ordinal))
        {
            if (byName.TryGetValue(name, out var definition))
                definitions.Add(definition);
        }

        return definitions;
    }

    private bool IsLoopGuardExempt(string toolName)
    {
        // undo_last/discard_change_set are called repeatedly with identical
        // arguments to peel back successive change sets; read tools are re-read
        // legitimately. Neither is a loop.
        if (string.Equals(toolName, "undo_last", StringComparison.Ordinal)
            || string.Equals(toolName, "discard_change_set", StringComparison.Ordinal))
        {
            return true;
        }

        return _toolRegistry.TryGet(toolName, out var tool)
               && tool is not null
               && tool.Definition.Risk == AgentToolRisk.Read;
    }

    private static bool IsAlwaysAvailableV2(string name) =>
        AgentToolPacks.IsAlwaysAvailable(name);
}

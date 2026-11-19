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

    private async Task<IReadOnlyList<AgentToolDefinition>> ApplyPendingToolSetAsync(
        string userText,
        int previousCount,
        CancellationToken ct)
    {
        // Keep the explicit request as a guarantee: those tools must be in the
        // next request even when the loaded set is already larger than the cap.
        var guaranteed = _sessionContext.PendingToolNames.ToList();
        foreach (var name in guaranteed)
            _sessionContext.TouchTool(name);

        _sessionContext.PendingToolNames.Clear();

        var definitions = await SelectToolDefinitionsAsync(userText, guaranteed, ct)
            .ConfigureAwait(false);
        SetActiveToolNames(definitions);
        SmartConLogger.Info(
            "Agent tool catalog changed: " + previousCount + " -> " + definitions.Count +
            " tools, cache invalidated");
        return definitions;
    }

    private async Task<IReadOnlyList<AgentToolDefinition>> SelectToolDefinitionsAsync(
        string userText,
        IReadOnlyCollection<string>? guaranteed,
        CancellationToken ct)
    {
        var all = _toolRegistry.GetDefinitions();
        if (all.Count == 0)
            return all;

        return await SelectCatalogV2ToolDefinitionsAsync(userText, all, guaranteed, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Catalog v2 turn set: the fixed core tools, the tools explicitly requested
    /// through load_tool_pack (guaranteed), the best dynamic matches for the
    /// user text, and then every loaded tool. The dynamic slots come from the
    /// decision model when it is enabled and fall back to deterministic card
    /// BM25 otherwise. Loaded tools are never capped out: a tool the model has
    /// already loaded or called must stay callable on the next turn, otherwise
    /// the provider refuses the call and the model ends the turn with a promise
    /// instead of a tool call (session d89546b8).
    /// </summary>
    private async Task<IReadOnlyList<AgentToolDefinition>> SelectCatalogV2ToolDefinitionsAsync(
        string userText,
        IReadOnlyList<AgentToolDefinition> all,
        IReadOnlyCollection<string>? guaranteed,
        CancellationToken ct)
    {
        const int topCount = 5;

        // A small catalog needs no retrieval: hiding tools from it would only cost
        // the model extra discovery rounds.
        if (all.Count <= 8)
            return all;

        var byName = new Dictionary<string, AgentToolDefinition>(StringComparer.Ordinal);
        foreach (var definition in all)
            byName[definition.Name] = definition;

        var selected = new List<AgentToolDefinition>(all.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Add(AgentToolDefinition definition)
        {
            if (names.Add(definition.Name))
                selected.Add(definition);
        }

        foreach (var definition in all)
        {
            if (IsAlwaysAvailableV2(definition.Name))
                Add(definition);
        }

        if (guaranteed is not null)
        {
            foreach (var name in guaranteed)
            {
                if (byName.TryGetValue(name, out var definition) && !names.Contains(name))
                    Add(definition);
            }
        }

        // Retrieval for the current text. It runs before the loaded set so a
        // continuation request can pull in the domain tools it names even when
        // the loaded set is already large.
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
            Add(definition);

        // Every loaded tool stays available. The catalog is still small (tens of
        // tools); when it grows, bound this list by recency and let
        // load_tool_pack bring evicted tools back.
        var loaded = _sessionContext.LoadedToolNames;
        for (var index = loaded.Count - 1; index >= 0; index--)
        {
            if (byName.TryGetValue(loaded[index], out var definition)
                && !names.Contains(definition.Name))
            {
                Add(definition);
            }
        }

        SmartConLogger.Debug(
            "Agent v2 tool catalog: " + selected.Count + "/" + all.Count + " tools, loaded=" +
            _sessionContext.LoadedToolNames.Count + ", guaranteed=" +
            (guaranteed?.Count ?? 0) + ", dynamic=" + (dynamicEnabled ? "decision" : "bm25"));

        return selected;
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

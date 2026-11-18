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
        // Loaded and pending tools count as available for find_tools, even before
        // the next round applies them to the request.
        var names = new HashSet<string>(
            definitions.Select(definition => definition.Name), StringComparer.Ordinal);
        foreach (var name in _sessionContext.LoadedToolNames)
            names.Add(name);
        foreach (var name in _sessionContext.PendingToolNames)
            names.Add(name);
        _sessionContext.ActiveToolNames = names.ToList();
    }

    private static int ElapsedMinutes(long startedAt) =>
        (int)((Stopwatch.GetTimestamp() - startedAt) * 60.0 / Stopwatch.Frequency / 60.0);

    private async Task<IReadOnlyList<AgentToolDefinition>> ApplyPendingToolSetAsync(
        string userText,
        int previousCount,
        CancellationToken ct)
    {
        foreach (var name in _sessionContext.PendingToolNames)
            _sessionContext.LoadedToolNames.Add(name);

        _sessionContext.PendingToolNames.Clear();

        var definitions = await SelectToolDefinitionsAsync(userText, ct).ConfigureAwait(false);
        SetActiveToolNames(definitions);
        SmartConLogger.Info(
            "Agent tool catalog changed: " + previousCount + " -> " + definitions.Count +
            " tools, cache invalidated");
        return definitions;
    }

    private async Task<IReadOnlyList<AgentToolDefinition>> SelectToolDefinitionsAsync(
        string userText,
        CancellationToken ct)
    {
        var all = _toolRegistry.GetDefinitions();
        if (all.Count == 0)
            return all;

        return SelectCatalogV2ToolDefinitions(userText, all);
    }

    /// <summary>
    /// Catalog v2 turn set: the fixed core tools plus everything loaded through
    /// load_tool_pack plus the best BM25 card matches for the user text. The full
    /// catalog is never sent; no BM25 signal degrades to core + loaded.
    /// </summary>
    private IReadOnlyList<AgentToolDefinition> SelectCatalogV2ToolDefinitions(
        string userText,
        IReadOnlyList<AgentToolDefinition> all)
    {
        const int maxTools = 16;
        const int topCount = 5;

        // A tiny catalog needs no retrieval: hiding tools from it would only cost
        // the model extra discovery rounds.
        if (all.Count <= 8)
            return all;

        var selected = new List<AgentToolDefinition>(maxTools);
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Add(AgentToolDefinition definition)
        {
            if (selected.Count >= maxTools || !names.Add(definition.Name))
                return;

            selected.Add(definition);
        }

        foreach (var definition in all)
        {
            if (IsAlwaysAvailableV2(definition.Name))
                Add(definition);
        }

        foreach (var definition in all)
        {
            if (_sessionContext.LoadedToolNames.Contains(definition.Name))
                Add(definition);
        }

        var rankable = all
            .Where(definition => !names.Contains(definition.Name))
            .ToList();
        foreach (var definition in ToolCardRouter.Rank(userText, rankable, topCount))
            Add(definition);

        SmartConLogger.Debug(
            "Agent v2 tool catalog: " + selected.Count + "/" + all.Count + " tools, loaded=" +
            _sessionContext.LoadedToolNames.Count);

        return selected;
    }

    private static bool IsAlwaysAvailableV2(string name) =>
        AgentToolPacks.IsCore(name)
        || string.Equals(name, LoadToolPackTool.ToolName, StringComparison.Ordinal)
        || string.Equals(name, ReadToolResultTool.ToolName, StringComparison.Ordinal);
}

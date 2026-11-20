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
                var result = await _toolExecutor
                    .ExecuteAsync(toolCall.Name, document.RootElement, ct)
                    .ConfigureAwait(false);
                return AttachPackSkillOnFirstWrite(toolCall.Name, result);
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

    private static string SerializeToolResult(string toolName, ToolResult result)
    {
        var json = ToolResultBudget.Serialize(result, ToolResultBudget.MaxCharactersFor(toolName));
        return AgentTextSanitizer.SanitizeJson(json);
    }

    /// <summary>
    /// A write tool can be callable before its pack was loaded: the initial shortlist
    /// ranks individual tools and carries no skill. Deliver the pack manual with the
    /// FIRST result of such a tool, so the model reads it before the apply call
    /// instead of relying on a prompt reminder (#400). The skill is marked as sent,
    /// so load_tool_pack will not repeat it.
    /// </summary>
    private ToolResult AttachPackSkillOnFirstWrite(string toolName, ToolResult result)
    {
        if (result.Skills.Count > 0
            || !_toolRegistry.TryGet(toolName, out var tool)
            || tool is null
            || tool.Definition.Risk == AgentToolRisk.Read)
        {
            return result;
        }

        var pack = AgentToolPacks.PackOfTool(toolName)?.Name;
        if (string.IsNullOrEmpty(pack)
            || !AgentToolPackSkills.TryGet(pack!, out var skill)
            || skill is null
            || _sessionContext.SentPackSkills.Contains(pack!))
        {
            return result;
        }

        SmartConLogger.Debug(
            "Agent pack skill attached to the first write result: pack=" + pack);
        return result.WithSkills(new[]
        {
            new ToolResultSkill { Pack = pack!, Markdown = skill }
        });
    }

    /// <summary>
    /// A skill is marked as sent only after its tool result was persisted: the
    /// model must not lose the manual because a write to the store failed.
    /// Tool calls of one assistant message are executed sequentially, so the
    /// first result marks the pack before the next one is built.
    /// </summary>
    private void MarkPackSkillsSent(ToolResult result)
    {
        foreach (var skill in result.Skills)
        {
            if (!string.IsNullOrEmpty(skill.Pack))
                _sessionContext.SentPackSkills.Add(skill.Pack!);
        }
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
        var maxCharacters = ToolResultBudget.MaxCharactersFor(toolCall.Name);
        string? handle = null;

        if (fullJson.Length > maxCharacters
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
        return fullJson.Length <= maxCharacters
            ? fullJson
            : ToolResultBudget.Trim(fullJson, maxCharacters, handle);
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
                SerializeToolResult(
                    string.Empty,
                    ToolResult.Fail(
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
        (int)((Stopwatch.GetTimestamp() - startedAt) / (double)Stopwatch.Frequency / 60.0);

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
    /// ordinal name order. The set starts with the always-available tools and is
    /// extended only by <c>load_tool_pack</c>: no retrieval runs on the user text.
    /// A huge first prompt cannot tell the router which tools the task needs — the
    /// model discovers them itself with short, targeted find_tools queries.
    /// </summary>
    private IReadOnlyList<AgentToolDefinition> ResolveToolDefinitions(
        string userText,
        CancellationToken ct)
    {
        _ = userText;
        _ = ct;
        var all = _toolRegistry.GetDefinitions();
        if (all.Count == 0)
            return all;

        if (!_sessionContext.ToolSetFrozen)
        {
            foreach (var name in SelectInitialToolNames())
                _sessionContext.FreezeTool(name);
            _sessionContext.ToolSetFrozen = true;

            SmartConLogger.Info(
                "Agent tool set frozen for the conversation: " +
                _sessionContext.FrozenToolNames.Count + "/" + all.Count + " tools");
        }

        var definitions = BuildFrozenDefinitions(all);
        SmartConLogger.Debug(
            "Agent v2 tool catalog: " + definitions.Count + "/" + all.Count +
            " frozen tools, loaded=" + _sessionContext.LoadedToolNames.Count);
        return definitions;
    }

    /// <summary>
    /// Initial tool set of a conversation: the always-available research core and
    /// system tools plus everything the session had already loaded. Pack tools are
    /// NEVER pre-selected from the first message: a long task prompt is a poor
    /// routing query, and a short one is not worth an agent run. The model calls
    /// find_tools with targeted queries and enables what it picked.
    /// </summary>
    private IEnumerable<string> SelectInitialToolNames()
    {
        foreach (var definition in _toolRegistry.GetDefinitions())
        {
            if (IsAlwaysAvailableV2(definition.Name))
                yield return definition.Name;
        }

        foreach (var name in _sessionContext.LoadedToolNames)
            yield return name;
        foreach (var name in _sessionContext.PendingToolNames)
            yield return name;
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

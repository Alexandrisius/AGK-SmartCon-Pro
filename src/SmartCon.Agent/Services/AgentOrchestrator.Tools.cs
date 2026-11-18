using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;

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

    private static List<AgentMessage> TrimHistory(IReadOnlyList<AgentMessage> history)
    {
        var start = System.Math.Max(0, history.Count - MaxHistoryMessages);
        while (start < history.Count && history[start].Role == AgentRole.Tool)
            start++;

        var result = new List<AgentMessage>(history.Count - start);
        for (var i = start; i < history.Count; i++)
            result.Add(history[i]);
        return result;
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
}

using System.Diagnostics;
using System.Text.Json;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.UI;

namespace SmartCon.Agent.Services;

public sealed class AgentOrchestrator
{
    private const int MaxToolRounds = 8;
    private const int MaxHistoryMessages = 120;

    private readonly IAgentLlmClient _llm;
    private readonly IToolExecutor _toolExecutor;
    private readonly IToolRegistry _toolRegistry;
    private readonly IAgentConversationStore _conversationStore;
    private readonly IAgentAuditLog _auditLog;
    private readonly IAgentProjectContextProvider _projectContextProvider;
    private readonly AgentLlmSettings _settings;
    private readonly AgentSessionContext _sessionContext;
    private readonly AgentPromptBuilder _promptBuilder;

    public AgentOrchestrator(
        IAgentLlmClient llm,
        IToolExecutor toolExecutor,
        IToolRegistry toolRegistry,
        IAgentConversationStore conversationStore,
        IAgentAuditLog auditLog,
        IAgentProjectContextProvider projectContextProvider,
        AgentLlmSettings settings,
        AgentSessionContext sessionContext,
        AgentPromptBuilder promptBuilder)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _projectContextProvider = projectContextProvider
            ?? throw new ArgumentNullException(nameof(projectContextProvider));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
    }

    public async Task<AgentTurnResult> RunTurnAsync(
        Guid conversationId,
        string userText,
        IReadOnlyList<AgentAttachment> attachments,
        IProgress<AgentProgress>? progress,
        CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope("Agent",
            ("Method", nameof(RunTurnAsync)),
            ("Session", conversationId.ToString("N")),
            ("Provider", _llm.ProviderName));

        var projectContext = await _projectContextProvider.GetAsync(ct).ConfigureAwait(false);
        _sessionContext.DocumentId = projectContext.DocumentId;
        _sessionContext.ConversationId = conversationId;

        var userMessage = AgentMessage.User(userText, attachments);
        await _conversationStore.AppendMessageAsync(conversationId, userMessage, ct).ConfigureAwait(false);

        var history = await _conversationStore.GetMessagesAsync(conversationId, ct).ConfigureAwait(false);
        var working = EnsureToolCallPairs(TrimHistory(history));
        var (compacted, _) = AgentHistoryCompactor.Compact(
            working, _settings.ContextWindowTokens);
        working = compacted;
        var systemMessage = AgentMessage.System(_promptBuilder.Build(projectContext));

        var usage = AgentUsage.Empty;
        var toolCallCount = 0;

        for (var round = 1; round <= MaxToolRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(AgentProgress.Status(
                round == 1
                    ? LanguageManager.GetString("Agent_Status_Thinking") ?? "Thinking…"
                    : LanguageManager.GetString("Agent_Status_Continuing") ?? "Continuing…"));

            var messages = new List<AgentMessage>(working.Count + 1) { systemMessage };
            messages.AddRange(working);

            var request = new AgentChatRequest
            {
                Model = _settings.Model,
                Messages = messages,
                Tools = _toolRegistry.GetAll().Select(tool => tool.Definition).ToList(),
                Temperature = _settings.Temperature,
                MaxOutputTokens = _settings.MaxOutputTokens
            };

            AgentChatResponse? completion = null;
            await foreach (var streamEvent in _llm.StreamAsync(request, ct).ConfigureAwait(false))
            {
                switch (streamEvent.Kind)
                {
                    case LlmStreamEventKind.TextDelta:
                        if (!string.IsNullOrEmpty(streamEvent.Text))
                            progress?.Report(AgentProgress.TextDelta(streamEvent.Text!));
                        break;

                    case LlmStreamEventKind.ReasoningDelta:
                        if (!string.IsNullOrEmpty(streamEvent.Text))
                            progress?.Report(AgentProgress.ReasoningDelta(streamEvent.Text!));
                        break;

                    case LlmStreamEventKind.ToolCallStarted:
                        progress?.Report(new AgentProgress
                        {
                            Kind = AgentProgressKind.ToolStarted,
                            ToolCallId = streamEvent.ToolCallId,
                            ToolName = streamEvent.ToolName
                        });
                        break;

                    case LlmStreamEventKind.Error:
                        throw new AgentLlmException(streamEvent.Error ?? "Unknown LLM stream error.");

                    case LlmStreamEventKind.Completed:
                        completion = streamEvent.Completion;
                        break;
                }
            }

            if (completion is null)
                throw new AgentLlmException("LLM stream ended without a completion event.");

            usage = Add(usage, completion.Usage);
            working.Add(completion.Message);
            await _conversationStore
                .AppendMessageAsync(conversationId, completion.Message, ct)
                .ConfigureAwait(false);

            if (completion.Message.ToolCalls.Count == 0)
            {
                var finalText = completion.Message.Content ?? string.Empty;
                progress?.Report(new AgentProgress
                {
                    Kind = AgentProgressKind.TurnCompleted,
                    Text = finalText,
                    Usage = usage,
                    ToolCallCount = toolCallCount
                });

                return new AgentTurnResult
                {
                    FinalText = finalText,
                    ToolCallCount = toolCallCount,
                    Rounds = round,
                    Usage = usage
                };
            }

            foreach (var toolCall in completion.Message.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                toolCallCount++;

                var startedAt = Stopwatch.GetTimestamp();
                var result = await ExecuteToolAsync(toolCall, ct).ConfigureAwait(false);
                var durationMs = (long)((Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency);

                var serializedResult = SerializeToolResult(result);
                var toolMessage = AgentMessage.ToolResult(toolCall.Id, serializedResult);
                working.Add(toolMessage);
                await _conversationStore
                    .AppendMessageAsync(conversationId, toolMessage, ct)
                    .ConfigureAwait(false);

                await WriteAuditAsync(toolCall, result, projectContext, durationMs, ct)
                    .ConfigureAwait(false);

                progress?.Report(new AgentProgress
                {
                    Kind = AgentProgressKind.ToolCompleted,
                    ToolCallId = toolCall.Id,
                    ToolName = toolCall.Name,
                    Success = result.Success,
                    Error = result.ErrorCode,
                    Text = result.Message,
                    Details = AgentToolResultFormatter.Pretty(serializedResult)
                });
            }
        }

        var limitMessage =
            (LanguageManager.GetString("Agent_Status_StepLimit") ?? "Step limit reached") +
            " (" + MaxToolRounds + ").";
        progress?.Report(new AgentProgress
        {
            Kind = AgentProgressKind.Error,
            Error = limitMessage,
            Usage = usage,
            ToolCallCount = toolCallCount
        });

        return new AgentTurnResult
        {
            FinalText = limitMessage,
            ToolCallCount = toolCallCount,
            Rounds = MaxToolRounds,
            Usage = usage,
            MaxRoundsReached = true
        };
    }

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

    private async Task WriteAuditAsync(
        AgentToolCall toolCall,
        ToolResult result,
        AgentProjectContext projectContext,
        long durationMs,
        CancellationToken ct)
    {
        try
        {
            await _auditLog.WriteAsync(new AgentAuditEntry
            {
                Actor = Environment.UserName,
                DocumentPath = projectContext.PathName,
                Tool = toolCall.Name,
                ArgsHash = AgentHashing.Sha256Hex(toolCall.ArgumentsJson),
                Result = result.Success ? "ok" : "error",
                AffectedCount = result.AffectedCount,
                DurationMs = durationMs,
                ErrorCode = result.ErrorCode
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent audit write failed: " + ex.GetType().Name + ": " + ex.Message +
                " [Action: check agent.db write access; the operation itself is not affected]");
        }
    }

    private static string SerializeToolResult(ToolResult result)
    {
        var json = ToolResultBudget.Serialize(result);
        return AgentTextSanitizer.Sanitize(json);
    }

    private static List<AgentMessage> TrimHistory(IReadOnlyList<AgentMessage> history)
    {
        var start = Math.Max(0, history.Count - MaxHistoryMessages);
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

    private static AgentUsage Add(AgentUsage left, AgentUsage right)
    {
        decimal? cost = null;
        if (left.CostUsd.HasValue || right.CostUsd.HasValue)
            cost = (left.CostUsd ?? 0m) + (right.CostUsd ?? 0m);

        return new AgentUsage
        {
            PromptTokens = left.PromptTokens + right.PromptTokens,
            CompletionTokens = left.CompletionTokens + right.CompletionTokens,
            ReasoningTokens = left.ReasoningTokens + right.ReasoningTokens,
            TotalTokens = left.TotalTokens + right.TotalTokens,
            CostUsd = cost
        };
    }
}
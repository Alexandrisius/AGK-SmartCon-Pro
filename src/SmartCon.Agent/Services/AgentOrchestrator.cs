using System.Diagnostics;
using System.Text.Json;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
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
    private readonly IAgentToolCallStore? _toolCallStore;
    private readonly IAgentUsageStore? _usageStore;
    private readonly IToolRouter? _toolRouter;

    public AgentOrchestrator(
        IAgentLlmClient llm,
        IToolExecutor toolExecutor,
        IToolRegistry toolRegistry,
        IAgentConversationStore conversationStore,
        IAgentAuditLog auditLog,
        IAgentProjectContextProvider projectContextProvider,
        AgentLlmSettings settings,
        AgentSessionContext sessionContext,
        AgentPromptBuilder promptBuilder,
        IAgentToolCallStore? toolCallStore = null,
        IAgentUsageStore? usageStore = null,
        IToolRouter? toolRouter = null)
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
        _toolCallStore = toolCallStore;
        _usageStore = usageStore;
        _toolRouter = toolRouter;
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
        if (!string.IsNullOrEmpty(_sessionContext.DocumentId)
            && !string.Equals(
                _sessionContext.DocumentId,
                projectContext.DocumentId,
                StringComparison.Ordinal)
            && _sessionContext.ApprovalMode != AgentApprovalMode.ConfirmWrites)
        {
            // The bypass / read-only choice is bound to one document: switching the
            // active document must never inherit a relaxed confirmation policy.
            _sessionContext.ApprovalMode = AgentApprovalMode.ConfirmWrites;
            SmartConLogger.Info(
                "Agent approval mode reset to ConfirmWrites because the active document changed");
        }

        _sessionContext.DocumentId = projectContext.DocumentId;
        _sessionContext.ConversationId = conversationId;

        var userMessage = AgentMessage.User(userText, attachments);
        await _conversationStore.AppendMessageAsync(conversationId, userMessage, ct).ConfigureAwait(false);

        var history = await _conversationStore.GetMessagesAsync(conversationId, ct).ConfigureAwait(false);
        var working = EnsureToolCallPairs(TrimHistory(history));
        var (compacted, _) = AgentHistoryCompactor.Compact(
            working, _settings.ContextWindowTokens, _settings.ContextThreshold);
        working = compacted;
        var systemMessage = AgentMessage.System(
            _promptBuilder.Build(projectContext, _sessionContext.ApprovalMode));
        var definitions = await SelectToolDefinitionsAsync(userText, ct);
        SetActiveToolNames(definitions);

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
                Tools = definitions,
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

                await WriteUsageAsync(conversationId, usage, ct).ConfigureAwait(false);

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

                await WriteToolCallAsync(
                        toolCall, result, serializedResult, conversationId, durationMs, ct)
                    .ConfigureAwait(false);

                // find_tools exists to unlock tools outside the routed shortlist:
                // the model must be able to call what it just discovered, so the
                // next round sends the full catalog.
                if (result.Success
                    && string.Equals(toolCall.Name, "find_tools", StringComparison.Ordinal))
                {
                    var fullCatalog = _toolRegistry.GetDefinitions();
                    if (ExpandsCatalog(definitions, fullCatalog))
                    {
                        definitions = fullCatalog;
                        SetActiveToolNames(definitions);
                        SmartConLogger.Debug(
                            "Agent expanded the tool catalog after find_tools: " +
                            definitions.Count + " definitions for the next round");
                    }
                }

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

        await WriteUsageAsync(conversationId, usage, ct).ConfigureAwait(false);

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

    private async Task WriteUsageAsync(Guid conversationId, AgentUsage usage, CancellationToken ct)
    {
        if (_usageStore is null)
            return;

        try
        {
            await _usageStore
                .RecordAsync(
                    conversationId.ToString("D"),
                    _llm.ProviderName,
                    _llm.Model,
                    usage,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent token_usage write failed: " + ex.GetType().Name + ": " + ex.Message +
                " [Action: check write access to agent.db; the turn itself is not affected]");
        }
    }

    private async Task WriteToolCallAsync(
        AgentToolCall toolCall,
        ToolResult result,
        string serializedResult,
        Guid conversationId,
        long durationMs,
        CancellationToken ct)
    {
        if (_toolCallStore is null)
            return;

        try
        {
            await _toolCallStore.RecordAsync(new AgentToolCallRecord
            {
                Id = toolCall.Id,
                SessionId = conversationId.ToString("D"),
                ToolName = toolCall.Name,
                ArgumentsJson = AgentRedactor.Redact(toolCall.ArgumentsJson),
                ResultJson = AgentRedactor.Redact(serializedResult),
                Status = result.Success ? "ok" : "error",
                Error = result.ErrorCode,
                DurationMs = durationMs
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent tool_calls write failed: " + ex.GetType().Name + ": " + ex.Message +
                " [Action: check write access to agent.db; the tool execution itself succeeded]");
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
        return AgentTextSanitizer.SanitizeJson(json);
    }

    private void SetActiveToolNames(IReadOnlyList<AgentToolDefinition> definitions) =>
        _sessionContext.ActiveToolNames =
            definitions.Select(definition => definition.Name).ToList();

    private static bool ExpandsCatalog(
        IReadOnlyList<AgentToolDefinition> current,
        IReadOnlyList<AgentToolDefinition> fullCatalog)
    {
        if (fullCatalog.Count != current.Count)
            return true;

        return fullCatalog.Any(candidate => !current.Any(existing =>
            string.Equals(existing.Name, candidate.Name, StringComparison.Ordinal)));
    }

    private async Task<IReadOnlyList<AgentToolDefinition>> SelectToolDefinitionsAsync(
        string userText,
        CancellationToken ct)
    {
        var all = _toolRegistry.GetDefinitions();
        if (_toolRouter is null || !_settings.UseToolRouter || all.Count == 0)
            return all;

        var decision = await _toolRouter.RouteAsync(userText, all, 16, ct).ConfigureAwait(false);
        if (decision.Fallback)
        {
            SmartConLogger.Debug(
                "Agent tool routing: no signal, full catalog (" + all.Count + " tools)");
            return all;
        }

        SmartConLogger.Debug(
            "Agent tool routing: " + decision.Tools.Count + "/" + all.Count + " tools, topScore " +
            decision.TopScore.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        return decision.Tools;
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
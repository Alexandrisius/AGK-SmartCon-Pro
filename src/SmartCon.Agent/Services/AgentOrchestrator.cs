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

public sealed partial class AgentOrchestrator
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
    private readonly IAgentWorkingStateStore? _workingStateStore;

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
        IToolRouter? toolRouter = null,
        IAgentWorkingStateStore? workingStateStore = null)
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
        _workingStateStore = workingStateStore;
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

        var turnStartedAt = Stopwatch.GetTimestamp();
        var usage = AgentUsage.Empty;
        var estimatedPromptTokens = 0;
        string? lastRequestId = null;
        WorkingState? workingState = null;

        try
        {
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
            var conversationChanged = _sessionContext.ConversationId != Guid.Empty
                && _sessionContext.ConversationId != conversationId;
            _sessionContext.ConversationId = conversationId;

            var userMessage = AgentMessage.User(userText, attachments);
            await _conversationStore
                .AppendMessageAsync(conversationId, userMessage, ct)
                .ConfigureAwait(false);

            if (_settings.WorkingStateEnabled)
            {
                // The cached state belongs to the previous conversation: never
                // leak its goal/constraints into a different chat.
                if (conversationChanged)
                    _sessionContext.WorkingState = null;

                workingState = await PrepareWorkingStateAsync(
                        conversationId, userText, projectContext, ct)
                    .ConfigureAwait(false);
            }

            var history = await _conversationStore
                .GetMessagesAsync(conversationId, ct)
                .ConfigureAwait(false);
            var working = EnsureToolCallPairs(TrimHistory(history));
            var systemMessage = AgentMessage.System(
                _promptBuilder.Build(projectContext, _sessionContext.ApprovalMode));
            var definitions = await SelectToolDefinitionsAsync(userText, ct);
            SetActiveToolNames(definitions);

            Func<IReadOnlyList<AgentMessage>, int>? estimator = null;
            if (_settings.ContextCompactionV2)
            {
                estimator = messages => _sessionContext.TokenCalibration.Apply(
                    AgentTokenEstimator.EstimateRequest(
                        systemMessage.Content, messages, definitions));
            }

            var (compacted, _) = AgentHistoryCompactor.Compact(
                working,
                _settings.ContextWindowTokens,
                _settings.ContextThreshold,
                AgentHistoryCompactor.KeepRecentToolResults,
                estimator);
            working = compacted;

            var toolCallCount = 0;

            for (var round = 1; round <= MaxToolRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                var messages = new List<AgentMessage>(working.Count + 1) { systemMessage };
                messages.AddRange(working);

                if (_settings.WorkingStateEnabled && workingState is not null)
                    ApplyTurnContext(messages, workingState, projectContext);

                var estimatedPrompt = AgentTokenEstimator.EstimateRequest(
                    systemMessage.Content, messages, definitions);
                var calibrated = _sessionContext.TokenCalibration.Apply(estimatedPrompt);
                estimatedPromptTokens += estimatedPrompt;
                SmartConLogger.Debug(
                    "Agent prompt estimate: raw=" + estimatedPrompt +
                    ", calibrated=" + calibrated +
                    ", messages=" + working.Count +
                    ", tools=" + definitions.Count);

                progress?.Report(AgentProgress.Status(
                    round == 1
                        ? LanguageManager.GetString("Agent_Status_Thinking") ?? "Thinking…"
                        : LanguageManager.GetString("Agent_Status_Continuing") ?? "Continuing…"));

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
                            throw new AgentLlmException(
                                streamEvent.Error ?? "Unknown LLM stream error.");

                        case LlmStreamEventKind.Completed:
                            completion = streamEvent.Completion;
                            break;
                    }
                }

                if (completion is null)
                    throw new AgentLlmException("LLM stream ended without a completion event.");

                usage = Add(usage, completion.Usage);
                lastRequestId = completion.RequestId ?? lastRequestId;
                if (completion.Usage.PromptTokens > 0)
                {
                    _sessionContext.TokenCalibration.Observe(
                        estimatedPrompt, completion.Usage.PromptTokens);

                    var deviationPercent =
                        (completion.Usage.PromptTokens - estimatedPrompt) * 100.0
                        / completion.Usage.PromptTokens;
                    SmartConLogger.Debug(
                        "Agent prompt estimate deviation: estimated=" + estimatedPrompt +
                        ", actual=" + completion.Usage.PromptTokens +
                        ", deviation=" + deviationPercent.ToString(
                            "F1", System.Globalization.CultureInfo.InvariantCulture) + "%");
                }

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

                    await WriteUsageAsync(
                        conversationId, usage, "ok", turnStartedAt,
                        lastRequestId, estimatedPromptTokens, ct).ConfigureAwait(false);

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
                    if (_settings.WorkingStateEnabled && workingState is not null)
                        AgentWorkingStateUpdater.ObserveToolResult(workingState, toolCall.Name, result);

                    var durationMs = (long)((Stopwatch.GetTimestamp() - startedAt)
                        * 1000.0 / Stopwatch.Frequency);

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

            await WriteUsageAsync(
                conversationId, usage, "ok", turnStartedAt,
                lastRequestId, estimatedPromptTokens, ct).ConfigureAwait(false);

            return new AgentTurnResult
            {
                FinalText = limitMessage,
                ToolCallCount = toolCallCount,
                Rounds = MaxToolRounds,
                Usage = usage,
                MaxRoundsReached = true
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await WriteUsageAsync(
                conversationId, usage, "cancelled", turnStartedAt,
                lastRequestId, estimatedPromptTokens, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await WriteUsageAsync(
                conversationId, usage, "error", turnStartedAt,
                lastRequestId, estimatedPromptTokens, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (_settings.WorkingStateEnabled
                && workingState is not null
                && _workingStateStore is not null)
            {
                await SaveWorkingStateAsync(conversationId, workingState).ConfigureAwait(false);
            }
        }
    }

    private async Task<WorkingState> PrepareWorkingStateAsync(
        Guid conversationId,
        string userText,
        AgentProjectContext projectContext,
        CancellationToken ct)
    {
        var state = _sessionContext.WorkingState;
        if (state is null && _workingStateStore is not null)
        {
            try
            {
                state = await _workingStateStore
                    .LoadAsync(conversationId, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    "Agent working state load failed: " + ex.GetType().Name + ": " + ex.Message +
                    " [Action: check read access to agent.db; the turn starts with an empty " +
                    "working state]");
            }
        }

        state ??= new WorkingState();
        AgentWorkingStateUpdater.UpdateFromUserText(state, userText);
        AgentWorkingStateUpdater.UpdateFromProjectContext(state, projectContext);
        _sessionContext.WorkingState = state;
        return state;
    }

    private async Task SaveWorkingStateAsync(Guid conversationId, WorkingState state)
    {
        try
        {
            await _workingStateStore!
                .SaveAsync(conversationId, state, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent working state save failed: " + ex.GetType().Name + ": " + ex.Message +
                " [Action: check write access to agent.db; the turn itself is not affected]");
        }
    }

    /// <summary>
    /// Prepends the rendered turn context to the last user message of the
    /// outgoing payload only. The message stored in the conversation history
    /// stays exactly as the user sent it.
    /// </summary>
    private static void ApplyTurnContext(
        List<AgentMessage> messages,
        WorkingState state,
        AgentProjectContext projectContext)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (message.Role != AgentRole.User)
                continue;

            var turnContext = AgentTurnContextBuilder.Build(state, projectContext);
            messages[index] = AgentMessage.User(
                turnContext + "\n\n" + (message.Content ?? string.Empty),
                message.Attachments);
            return;
        }
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
}
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
    private readonly IAgentWorkingStateStore? _workingStateStore;
    private readonly IAgentToolResultBlobStore? _toolResultBlobStore;
    private readonly IAgentContextCompactor? _contextCompactor;
    private readonly IAgentCompactionStore? _compactionStore;
    private readonly IDynamicToolRanker? _dynamicToolRanker;

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
        IAgentWorkingStateStore? workingStateStore = null,
        IAgentToolResultBlobStore? toolResultBlobStore = null,
        IAgentContextCompactor? contextCompactor = null,
        IAgentCompactionStore? compactionStore = null,
        IDynamicToolRanker? dynamicToolRanker = null)
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
        _workingStateStore = workingStateStore;
        _toolResultBlobStore = toolResultBlobStore;
        _contextCompactor = contextCompactor;
        _compactionStore = compactionStore;
        _dynamicToolRanker = dynamicToolRanker;
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
        var lastContextTokens = 0;
        var lastContextLimit = 0;
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
            if (conversationChanged)
            {
                // Loaded packs belong to the previous conversation: never leak its
                // tool set into a different chat.
                _sessionContext.ResetToolSet();
            }

            var userMessage = AgentMessage.User(userText, attachments);
            await _conversationStore
                .AppendMessageAsync(conversationId, userMessage, ct)
                .ConfigureAwait(false);

            // The cached state belongs to the previous conversation: never
            // leak its goal/constraints into a different chat.
            if (conversationChanged)
                _sessionContext.WorkingState = null;

            workingState = await PrepareWorkingStateAsync(
                    conversationId, userText, projectContext, ct)
                .ConfigureAwait(false);

            var history = await _conversationStore
                .GetMessagesAsync(conversationId, ct)
                .ConfigureAwait(false);
            var rawHistory = history.ToList();
            var persistedMessageCount = history.Count;
            var checkpoint = await LoadCheckpointAsync(conversationId, ct).ConfigureAwait(false);
            var baseRowId = 0;
            IReadOnlyList<AgentMessage> effectiveHistory = history;
            if (checkpoint is not null && AgentCompactionHash.Validate(checkpoint, history))
            {
                baseRowId = checkpoint.ToRowId;
                effectiveHistory = history.Skip(checkpoint.ToRowId).ToList();
            }
            else if (checkpoint is not null)
            {
                SmartConLogger.Warn(
                    "Agent context: ignored checkpoint " + checkpoint.Id +
                    " because its source prefix changed [Action: the raw history is used; " +
                    "the next turn can create a fresh checkpoint]");
            }

            // No message-count cap: dropping the oldest messages silently is what
            // made a long chat lose its first instruction. The compactor replaces
            // the old prefix with a checkpoint and keeps a token-bounded tail.
            var working = EnsureToolCallPairs(effectiveHistory);
            if (baseRowId > 0)
            {
                working.Insert(0, AgentMessage.System(
                    "<conversation_checkpoint>\n" + checkpoint!.Summary +
                    "\n</conversation_checkpoint>"));
                SmartConLogger.Info(
                    "Agent context: restored checkpoint " + checkpoint!.Id +
                    " (" + checkpoint.ToRowId + " messages covered)");
            }

            var systemMessage = AgentMessage.System(
                _promptBuilder.Build(projectContext, _sessionContext.ApprovalMode));
            var definitions = await SelectToolDefinitionsAsync(
                    userText,
                    _sessionContext.PendingToolNames.Count == 0
                        ? null
                        : _sessionContext.PendingToolNames.ToList(),
                    ct)
                .ConfigureAwait(false);
            SetActiveToolNames(definitions);

            var lastActualPromptTokens = 0;
            var lastRequestedWorkingCount = 0;
            AgentCompactionResult? lastCompaction = null;

            Func<IReadOnlyList<AgentMessage>, int> rawEstimator = messages =>
                _sessionContext.TokenCalibration.Apply(
                    AgentTokenEstimator.EstimateRequest(
                        systemMessage.Content, messages, definitions));

            Func<IReadOnlyList<AgentMessage>, int> estimator = messages =>
            {
                var calibrated = rawEstimator(messages);

                // The provider's last prompt_tokens is the floor: a local
                // estimate that came 20-30 % short must not let the next request
                // cross the trigger unnoticed. It applies only before
                // compaction; the compactor measures the shrunken prompt with
                // rawEstimator.
                if (lastActualPromptTokens > 0 && messages.Count > lastRequestedWorkingCount)
                {
                    var floor = lastActualPromptTokens;
                    for (var index = lastRequestedWorkingCount; index < messages.Count; index++)
                        floor += AgentTokenEstimator.Estimate(messages[index]);
                    if (floor > calibrated)
                        calibrated = floor;
                }

                return calibrated;
            };

            var projected = await ProjectHistoryAsync(
                    working, workingState, conversationId, baseRowId, rawHistory,
                    estimator, rawEstimator, ct)
                .ConfigureAwait(false);
            working = projected.Messages;
            if (projected.Changed)
                lastCompaction = projected;

            var allowCheckpoint = true;
            var toolCallCount = 0;
            var loopGuard = new LoopGuard();
            var loopAborted = false;

            // No round or time cap: the turn runs until the model stops, the user
            // presses Stop (ct) or the loop guard aborts a repeating call pattern.
            for (var round = 1; ; round++)
            {
                ct.ThrowIfCancellationRequested();
                var elapsedMinutes = ElapsedMinutes(turnStartedAt);

                if (_sessionContext.PendingToolNames.Count > 0)
                {
                    definitions = await ApplyPendingToolSetAsync(
                            userText, definitions.Count, ct)
                        .ConfigureAwait(false);
                }

                var turnContextBase = workingState is null
                    ? string.Empty
                    : AgentTurnContextBuilder.Build(workingState, projectContext);
                var reserveTokens = AgentTokenEstimator.EstimateText(turnContextBase)
                                    + AgentContextStatus.EstimatedTokens + 160;

                var compaction = await CompactContextAsync(
                        working, workingState, conversationId, baseRowId, reserveTokens,
                        rawHistory, allowCheckpoint, estimator, rawEstimator, ct)
                    .ConfigureAwait(false);
                working = compaction.Messages;
                if (compaction.Changed)
                {
                    lastCompaction = compaction;

                    // The provider-reported prompt_tokens describes the prompt
                    // before this compaction. Keeping it as the floor for the
                    // shrunken prompt would make every following round look
                    // larger than it is.
                    lastActualPromptTokens = 0;
                    lastRequestedWorkingCount = 0;
                }

                if (compaction.CoveredToRowId > baseRowId)
                    baseRowId = compaction.CoveredToRowId;

                if (compaction.Fallback
                    && !compaction.Summarized
                    && compaction.DroppedCount > 0)
                {
                    // The in-memory tail no longer maps to a contiguous raw
                    // prefix; a checkpoint created from it would be invalid.
                    allowCheckpoint = false;
                }

                var contextLimit = (int)(Math.Max(
                                             1000,
                                             _settings.ContextWindowTokens - reserveTokens) *
                                         Math.Max(0.3, Math.Min(_settings.ContextThreshold, 0.95)));
                // Measured on the same working set the compactor triggers on;
                // the turn context/status reserve is internal headroom.
                var contextTokens = rawEstimator(working);
                lastContextTokens = contextTokens;
                lastContextLimit = contextLimit;

                var messages = new List<AgentMessage>(working.Count + 2) { systemMessage };
                messages.AddRange(working);

                if (!string.IsNullOrEmpty(turnContextBase))
                {
                    ApplyTurnContext(
                        messages, turnContextBase, round, elapsedMinutes,
                        contextTokens, contextLimit);
                }

                messages.Add(AgentContextStatus.BuildMessage(
                    round, elapsedMinutes, contextTokens, contextLimit, lastCompaction));
                progress?.Report(AgentProgress.ContextUsage(
                    contextTokens, contextLimit, lastCompaction));
                lastCompaction = null;

                var requestedWorkingCount = working.Count;
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
                    lastActualPromptTokens = completion.Usage.PromptTokens;
                    lastRequestedWorkingCount = requestedWorkingCount;

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
                else
                {
                    // No usage: the previous floor no longer describes this request.
                    lastActualPromptTokens = 0;
                }

                var persistedCompletion =
                    completion.Message.WithSessionIndex(persistedMessageCount + 1);
                persistedMessageCount++;
                working.Add(persistedCompletion);
                rawHistory.Add(persistedCompletion);
                await _conversationStore
                    .AppendMessageAsync(conversationId, persistedCompletion, ct)
                    .ConfigureAwait(false);

                if (persistedCompletion.ToolCalls.Count == 0)
                {
                    var finalText = persistedCompletion.Content ?? string.Empty;
                    progress?.Report(new AgentProgress
                    {
                        Kind = AgentProgressKind.TurnCompleted,
                        Text = finalText,
                        Usage = usage,
                        ToolCallCount = toolCallCount
                    });

                    await WriteUsageAsync(
                        conversationId, usage, "ok", turnStartedAt,
                        lastRequestId, estimatedPromptTokens, lastContextTokens, lastContextLimit, ct).ConfigureAwait(false);

                    return new AgentTurnResult
                    {
                        FinalText = finalText,
                        ToolCallCount = toolCallCount,
                        Rounds = round,
                        Usage = usage
                    };
                }

                foreach (var toolCall in persistedCompletion.ToolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    toolCallCount++;

                    var startedAt = Stopwatch.GetTimestamp();
                    ToolResult result;
                    if (loopAborted)
                    {
                        result = LoopAbortedResult(toolCall.Name);
                    }
                    else
                    {
                        var argumentsJson = toolCall.ArgumentsJson ?? "{}";
                        var exempt = IsLoopGuardExempt(toolCall.Name);
                        var guard = loopGuard.BeforeCall(toolCall.Name, argumentsJson, exempt);
                        if (guard.Action == LoopGuardAction.Abort)
                        {
                            loopAborted = true;
                            SmartConLogger.Info(
                                "Agent loop guard aborted the turn: tool=" + toolCall.Name +
                                ", identicalCalls=" + guard.IdenticalCalls);
                            result = LoopAbortedResult(toolCall.Name);
                        }
                        else if (guard.Action == LoopGuardAction.Block)
                        {
                            SmartConLogger.Info(
                                "Agent loop guard blocked the tool call: tool=" + toolCall.Name +
                                ", identicalCalls=" + guard.IdenticalCalls);
                            result = ToolResult.Fail(
                                "loop_detected",
                                guard.Hint ?? DefaultLoopBlockedHint,
                                data: JsonSerializer.SerializeToElement(new
                                {
                                    identicalCalls = guard.IdenticalCalls
                                }));
                        }
                        else
                        {
                            result = await ExecuteToolAsync(toolCall, ct).ConfigureAwait(false);
                            var observed = loopGuard.ObserveResult(
                                toolCall.Name, argumentsJson, result.Success, exempt);
                            if (observed.WarnOnly && !result.Success)
                            {
                                SmartConLogger.Info(
                                    "Agent loop guard warning: tool=" + toolCall.Name +
                                    ", identicalErrors=" + observed.IdenticalErrors);
                                if (!string.IsNullOrEmpty(observed.Hint))
                                    result = result.WithWarnings(new[] { observed.Hint! });
                            }
                        }
                    }

                    if (workingState is not null)
                        AgentWorkingStateUpdater.ObserveToolResult(workingState, toolCall.Name, result);

                    var durationMs = (long)((Stopwatch.GetTimestamp() - startedAt)
                        * 1000.0 / Stopwatch.Frequency);

                    var serializedResult =
                        await SerializeToolResultAsync(toolCall, result, ct).ConfigureAwait(false);
                    var toolMessage = AgentMessage
                        .ToolResult(toolCall.Id, serializedResult)
                        .WithSessionIndex(persistedMessageCount + 1);
                    persistedMessageCount++;
                    working.Add(toolMessage);
                    rawHistory.Add(toolMessage);
                    await _conversationStore
                        .AppendMessageAsync(conversationId, toolMessage, ct)
                        .ConfigureAwait(false);

                    await WriteAuditAsync(toolCall, result, projectContext, durationMs, ct)
                        .ConfigureAwait(false);

                    await WriteToolCallAsync(
                            toolCall, result, serializedResult, conversationId, durationMs, ct)
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

                    if (loopAborted)
                        break;
                }

                if (loopAborted)
                {
                    var abortMessage =
                        (LanguageManager.GetString("Agent_Status_LoopAborted")
                         ?? "Stopped: repeated tool calls were detected") +
                        " (loop_aborted).";
                    progress?.Report(new AgentProgress
                    {
                        Kind = AgentProgressKind.TurnCompleted,
                        Text = abortMessage,
                        Usage = usage,
                        ToolCallCount = toolCallCount
                    });

                    await WriteUsageAsync(
                        conversationId, usage, "ok", turnStartedAt,
                        lastRequestId, estimatedPromptTokens, lastContextTokens, lastContextLimit, ct).ConfigureAwait(false);

                    return new AgentTurnResult
                    {
                        FinalText = abortMessage,
                        ToolCallCount = toolCallCount,
                        Rounds = round,
                        Usage = usage
                    };
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await WriteUsageAsync(
                conversationId, usage, "cancelled", turnStartedAt,
                lastRequestId, estimatedPromptTokens, lastContextTokens, lastContextLimit,
                CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await WriteUsageAsync(
                conversationId, usage, "error", turnStartedAt,
                lastRequestId, estimatedPromptTokens, lastContextTokens, lastContextLimit,
                CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (workingState is not null && _workingStateStore is not null)
            {
                await SaveWorkingStateAsync(conversationId, workingState).ConfigureAwait(false);
            }
        }
    }
}
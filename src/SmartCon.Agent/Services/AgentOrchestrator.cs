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
    private readonly IAgentWorkingStateStore? _workingStateStore;
    private readonly IAgentToolResultBlobStore? _toolResultBlobStore;

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
        IAgentToolResultBlobStore? toolResultBlobStore = null)
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
            var working = EnsureToolCallPairs(TrimHistory(history));
            var systemMessage = AgentMessage.System(
                _promptBuilder.Build(projectContext, _sessionContext.ApprovalMode));
            var definitions = await SelectToolDefinitionsAsync(userText, ct);
            SetActiveToolNames(definitions);

            Func<IReadOnlyList<AgentMessage>, int> estimator = messages =>
                _sessionContext.TokenCalibration.Apply(
                    AgentTokenEstimator.EstimateRequest(
                        systemMessage.Content, messages, definitions));

            var (compacted, _) = AgentHistoryCompactor.Compact(
                working,
                _settings.ContextWindowTokens,
                _settings.ContextThreshold,
                AgentHistoryCompactor.KeepRecentToolResults,
                estimator);
            working = compacted;

            var toolCallCount = 0;
            var loopGuard = new LoopGuard();
            var loopAborted = false;

            for (var round = 1; round <= MaxToolRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                if (_sessionContext.PendingToolNames.Count > 0)
                {
                    definitions = await ApplyPendingToolSetAsync(
                            userText, definitions.Count, ct)
                        .ConfigureAwait(false);
                }

                var messages = new List<AgentMessage>(working.Count + 1) { systemMessage };
                messages.AddRange(working);

                if (workingState is not null)
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
                    ToolResult result;
                    if (loopAborted)
                    {
                        result = LoopAbortedResult(toolCall.Name);
                    }
                    else
                    {
                        var argumentsJson = toolCall.ArgumentsJson ?? "{}";
                        var guard = loopGuard.BeforeCall(toolCall.Name, argumentsJson);
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
                                toolCall.Name, argumentsJson, result.Success);
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
                        lastRequestId, estimatedPromptTokens, ct).ConfigureAwait(false);

                    return new AgentTurnResult
                    {
                        FinalText = abortMessage,
                        ToolCallCount = toolCallCount,
                        Rounds = round,
                        Usage = usage
                    };
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
            if (workingState is not null && _workingStateStore is not null)
            {
                await SaveWorkingStateAsync(conversationId, workingState).ConfigureAwait(false);
            }
        }
    }

    private void SetActiveToolNames(IReadOnlyList<AgentToolDefinition> definitions) =>
        _sessionContext.ActiveToolNames =
            definitions.Select(definition => definition.Name).ToList();

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
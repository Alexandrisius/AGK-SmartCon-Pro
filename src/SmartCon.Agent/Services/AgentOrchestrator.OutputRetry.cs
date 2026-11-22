using System.Globalization;
using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.UI;

namespace SmartCon.Agent.Services;

public sealed partial class AgentOrchestrator
{
    /// <summary>
    /// How often a round is re-requested after the provider stopped at the
    /// output token limit (finish_reason "length"). The truncated completion is
    /// discarded: nothing is persisted and its tool calls are never executed.
    /// </summary>
    private const int MaxOutputCutRetries = 2;

    private sealed class RoundCompletion
    {
        public required AgentChatResponse Completion { get; init; }

        /// <summary>Usage of every attempt, including the discarded ones.</summary>
        public AgentUsage Usage { get; init; } = AgentUsage.Empty;

        public string? RequestId { get; init; }

        public bool HasActualPromptTokens { get; init; }

        public int PromptTokens { get; init; }
    }

    private async Task<RoundCompletion> RequestRoundCompletionAsync(
        List<AgentMessage> messages,
        IReadOnlyList<AgentToolDefinition> definitions,
        Guid conversationId,
        int round,
        int elapsedMinutes,
        int contextTokens,
        int contextLimit,
        AgentCompactionResult? roundCompaction,
        int estimatedPrompt,
        string staticHash,
        string toolSetHash,
        IProgress<AgentProgress>? progress,
        CancellationToken ct)
    {
        AgentChatRequest BuildRequest(int maxOutputTokens) => new()
        {
            Model = _settings.Model,
            Messages = messages,
            Tools = definitions,
            Temperature = _settings.Temperature,
            MaxOutputTokens = maxOutputTokens,
            ConversationId = conversationId,
            CacheKey = conversationId.ToString("N")
        };

        var usage = AgentUsage.Empty;
        string? requestId = null;
        var hasActualPromptTokens = false;
        var promptTokens = 0;
        var outputCut = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Only the second retry raises the budget: the first retry just asks
            // for a compact reply through the ephemeral status message.
            var budget = outputCut >= MaxOutputCutRetries
                ? Math.Min(_settings.MaxOutputTokens * 2, AgentLlmSettings.MaxMaxOutputTokens)
                : _settings.MaxOutputTokens;

            AgentChatResponse? completion = null;
            await foreach (var streamEvent in _llm.StreamAsync(
                    BuildRequest(budget), ct).ConfigureAwait(false))
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
            if (completion.RequestId is not null)
                requestId = completion.RequestId;
            SmartConLogger.Info(AgentCacheLog.Build(
                round, completion.RequestId, completion.Usage, staticHash, toolSetHash));
            if (completion.Usage.PromptTokens > 0)
            {
                hasActualPromptTokens = true;
                promptTokens = completion.Usage.PromptTokens;

                _sessionContext.TokenCalibration.Observe(
                    estimatedPrompt, completion.Usage.PromptTokens);

                var deviationPercent =
                    (completion.Usage.PromptTokens - estimatedPrompt) * 100.0
                    / completion.Usage.PromptTokens;
                SmartConLogger.Debug(
                    "Agent prompt estimate deviation: estimated=" + estimatedPrompt +
                    ", actual=" + completion.Usage.PromptTokens +
                    ", deviation=" + deviationPercent.ToString(
                        "F1", CultureInfo.InvariantCulture) + "%");
            }
            else
            {
                // No usage: the previous floor no longer describes this request.
                hasActualPromptTokens = false;
            }

            if (!completion.Truncated)
            {
                return new RoundCompletion
                {
                    Completion = completion,
                    Usage = usage,
                    RequestId = requestId,
                    HasActualPromptTokens = hasActualPromptTokens,
                    PromptTokens = promptTokens
                };
            }

            outputCut++;
            if (outputCut > MaxOutputCutRetries)
            {
                throw new AgentLlmException(
                    "The response was cut off by the output token limit after " +
                    MaxOutputCutRetries + " retries. " +
                    "Raise Max output tokens in the agent settings.");
            }

            SmartConLogger.Warn(
                "Agent round " + round + ": the reply was cut off by the output token " +
                "limit, discarded and re-requested (" + outputCut + "/" + MaxOutputCutRetries +
                ") [Action: the retry asks the model for a compact reply; if it repeats, " +
                "raise Max output tokens in the agent settings]");

            // The stream may already have shown tool cards for the discarded
            // call attempts: close them, or the panel keeps a "running" card
            // for a call that will never execute.
            var cutText = LanguageManager.GetString("Agent_Status_OutputCutRetry")
                ?? "Reply cut off by the output limit — retrying…";
            foreach (var call in completion.Message.ToolCalls)
            {
                progress?.Report(new AgentProgress
                {
                    Kind = AgentProgressKind.ToolCompleted,
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    Success = false,
                    Error = "output_limit",
                    Text = cutText
                });
            }

            progress?.Report(AgentProgress.Status(cutText));

            // Extend the ephemeral status of this round with the cut marker and
            // the reminder instead of the discarded completion.
            messages.RemoveAt(messages.Count - 1);
            messages.Add(AgentContextStatus.BuildMessage(
                round, elapsedMinutes, contextTokens, contextLimit, roundCompaction, outputCut));
        }
    }
}

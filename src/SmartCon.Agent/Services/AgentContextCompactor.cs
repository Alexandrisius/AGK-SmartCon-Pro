using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services;

/// <summary>
/// Context ladder (runbook H1-15/H1-16). Step 1 masks old tool results with a
/// digest that keeps the result handle; step 2 replaces the old prefix with a
/// nine-section checkpoint and keeps a token-bounded recent tail; step 3
/// hard-shrinks to the recent tail. Failures of the summarizer fall back to
/// masking, and three consecutive failures disable the summary step for this
/// instance (circuit breaker).
/// </summary>
public sealed partial class AgentContextCompactor : IAgentContextCompactor
{
    private const int CircuitBreakerThreshold = 3;

    /// <summary>
    /// Turn-start projection only runs once the raw history reaches the compaction
    /// limit. The owner asked for no context reduction before ~500k, so this used
    /// to be 0.75 of the limit (≈262k at a 500k window) and hid results the agent
    /// was still using.
    /// </summary>
    private const double ProjectionPressureRatio = 1.0;

    private readonly IAgentSummarizer _summarizer;
    private readonly IAgentCompactionStore? _store;
    private readonly int _summaryMaxInputTokens;
    private readonly int _summaryMaxOutputTokens;

    private int _consecutiveFailures;

    public AgentContextCompactor(
        IAgentSummarizer summarizer,
        AgentLlmSettings? settings = null,
        IAgentCompactionStore? store = null)
    {
        _summarizer = summarizer ?? throw new ArgumentNullException(nameof(summarizer));
        _store = store;
        _summaryMaxInputTokens = settings?.SummaryMaxInputTokens ?? 24_000;
        _summaryMaxOutputTokens = settings?.SummaryMaxOutputTokens ?? 1_500;
    }

    public Task<AgentCompactionResult> ProjectAsync(
        AgentCompactionRequest request,
        CancellationToken ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        ct.ThrowIfCancellationRequested();

        var messages = request.Messages.ToList();
        var tokensBefore = Estimate(request, messages, afterCompaction: true);

        // No pressure, no projection: the raw history is cheaper to keep than to
        // hide, and an unmotivated shrink looks like lost context to the user.
        if (tokensBefore <= ComputeLimit(request) * ProjectionPressureRatio)
            return Task.FromResult(Unchanged(messages, tokensBefore));

        var masked = MaskOldToolResults(
            messages, request.KeepRecentToolResults, out var maskedCount);
        if (maskedCount == 0)
            return Task.FromResult(Unchanged(messages, tokensBefore));

        var tokensAfter = Estimate(request, masked, afterCompaction: true);
        if (tokensBefore - tokensAfter < Math.Max(0, request.MinMaskSavingsTokens))
            return Task.FromResult(Unchanged(messages, tokensBefore));

        SmartConLogger.Info(
            "Agent context: projected " + maskedCount + " old tool result(s), " +
            tokensBefore + " -> " + tokensAfter + " tokens");

        return Task.FromResult(new AgentCompactionResult
        {
            Messages = masked,
            MaskedCount = maskedCount,
            TokensBefore = tokensBefore,
            TokensAfter = tokensAfter,
            SkillPacksToResend = CollectSkillPacksToResend(messages, masked)
        });
    }

    public async Task<AgentCompactionResult> CompactAsync(
        AgentCompactionRequest request,
        CancellationToken ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        ct.ThrowIfCancellationRequested();

        var messages = request.Messages.ToList();
        var tokensBefore = Estimate(request, messages);
        var limit = ComputeLimit(request);
        if (tokensBefore <= limit)
            return Unchanged(messages, tokensBefore);

        var masked = MaskOldToolResults(
            messages, request.KeepRecentToolResults, out var maskedCount);
        var maskedTokens = Estimate(request, masked, afterCompaction: true);
        if (maskedTokens <= limit)
        {
            SmartConLogger.Info(
                "Agent context: masking only, " + maskedCount + " tool result(s), " +
                tokensBefore + " -> " + maskedTokens + " tokens (limit " + limit + ")");
            return new AgentCompactionResult
            {
                Messages = masked,
                MaskedCount = maskedCount,
                TokensBefore = tokensBefore,
                TokensAfter = maskedTokens,
                SkillPacksToResend = CollectSkillPacksToResend(messages, masked)
            };
        }

        if (!request.AllowCheckpoint)
        {
            var hard = HardShrink(masked, request);
            var hardTokens = Estimate(request, hard.Messages, afterCompaction: true);
            SmartConLogger.Info(
                "Agent context: summary disabled for the rest of the turn, hard shrink " +
                "dropped=" + hard.Dropped + ", " + tokensBefore + " -> " + hardTokens +
                " tokens");
            return new AgentCompactionResult
            {
                Messages = hard.Messages,
                MaskedCount = maskedCount,
                DroppedCount = hard.Dropped,
                TokensBefore = tokensBefore,
                TokensAfter = hardTokens,
                Fallback = true,
                FallbackReason = "summary_disabled",
                CoveredToRowId = request.BaseRowId,
                SkillPacksToResend = CollectSkillPacksToResend(messages, hard.Messages)
            };
        }

        if (_consecutiveFailures < CircuitBreakerThreshold)
        {
            try
            {
                var cut = ComputeCut(masked, request);
                if (cut <= 0 || cut >= masked.Count)
                    return NothingToSummarize(
                        masked, tokensBefore, maskedTokens, maskedCount,
                        CollectSkillPacksToResend(messages, masked));

                var summary = await SummarizeCompactionAsync(
                        masked.Take(cut).ToList(), request, ct)
                    .ConfigureAwait(false);
                _consecutiveFailures = 0;

                var pinned = BuildPinnedBlock(request.WorkingState);
                var checkpointText = string.IsNullOrWhiteSpace(pinned)
                    ? summary
                    : summary + "\n\n" + pinned;

                var recordCut = cut;
                var (checkpointed, dropped) = BuildCheckpoint(masked, checkpointText, cut);
                var checkpointTokens = Estimate(request, checkpointed, afterCompaction: true);

                if (checkpointTokens > limit)
                {
                    var hardCut = Math.Min(
                        masked.Count - 1,
                        Math.Max(cut, masked.Count - Math.Max(1, request.HardShrinkMessages)));
                    hardCut = AlignCut(masked, hardCut);
                    if (hardCut > cut)
                    {
                        summary = await SummarizeCompactionAsync(
                                masked.Take(hardCut).ToList(), request, ct)
                            .ConfigureAwait(false);
                        checkpointText = string.IsNullOrWhiteSpace(pinned)
                            ? summary
                            : summary + "\n\n" + pinned;
                        (checkpointed, dropped) = BuildCheckpoint(
                            masked, checkpointText, hardCut);
                        checkpointTokens = Estimate(request, checkpointed, afterCompaction: true);
                        recordCut = hardCut;
                    }

                    if (checkpointTokens > limit)
                    {
                        SmartConLogger.Warn(
                            "Agent context: the checkpoint is still " + checkpointTokens +
                            " tokens, above the " + limit + " limit after a hard shrink " +
                            "[Action: the turn continues with the checkpoint; raise the " +
                            "context window or lower the request size if the provider rejects it]");
                    }
                }

                var persistedPrefix = messages.Take(recordCut).ToList();
                var persisted = await PersistCheckpointAsync(
                        request, persistedPrefix, checkpointText, tokensBefore,
                        checkpointTokens, ct)
                    .ConfigureAwait(false);
                var record = persisted.Record;
                var coveredToRowId = persisted.ToRowId;

                SmartConLogger.Info(
                    "Agent context: checkpoint summary, dropped=" + dropped +
                    ", masked=" + maskedCount + ", " + tokensBefore + " -> " +
                    checkpointTokens + " tokens (limit " + limit + "), " +
                    "covered=" + coveredToRowId +
                    (record is null ? " (not persisted)" : string.Empty));

                return new AgentCompactionResult
                {
                    Messages = checkpointed,
                    Summary = checkpointText,
                    MaskedCount = maskedCount,
                    DroppedCount = dropped,
                    TokensBefore = tokensBefore,
                    TokensAfter = checkpointTokens,
                    Summarized = true,
                    CoveredToRowId = coveredToRowId,
                    Fallback = checkpointTokens > limit,
                    FallbackReason = checkpointTokens > limit ? "hard_shrink" : null,
                    Record = record,
                    SkillPacksToResend = CollectSkillPacksToResend(messages, checkpointed)
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                SmartConLogger.Warn(
                    "Agent context: summarizer failed (" + _consecutiveFailures + "/" +
                    CircuitBreakerThreshold + "): " + ex.GetType().Name + ": " + ex.Message +
                    " [Action: the ladder falls back to masking/hard shrink; after " +
                    CircuitBreakerThreshold + " failures summaries are skipped this session]");

                if (_consecutiveFailures >= CircuitBreakerThreshold)
                {
                    SmartConLogger.Warn(
                        "Agent context: summarizer circuit breaker opened after " +
                        CircuitBreakerThreshold + " failures [Action: check the summary model " +
                        "and provider key; the agent continues with masking and hard shrink]");
                }
            }
        }

        var hardShrink = HardShrink(masked, request);
        var fallbackTokens = Estimate(request, hardShrink.Messages, afterCompaction: true);
        SmartConLogger.Info(
            "Agent context: fallback hard shrink, dropped=" + hardShrink.Dropped +
            ", " + tokensBefore + " -> " + fallbackTokens + " tokens");

        return new AgentCompactionResult
        {
            Messages = hardShrink.Messages,
            MaskedCount = maskedCount,
            DroppedCount = hardShrink.Dropped,
            TokensBefore = tokensBefore,
            TokensAfter = fallbackTokens,
            Fallback = true,
            FallbackReason = "summary_unavailable",
            SkillPacksToResend = CollectSkillPacksToResend(messages, hardShrink.Messages)
        };
    }

    private static int ComputeLimit(AgentCompactionRequest request)
    {
        var budget = Math.Max(
            1000,
            request.ContextBudgetTokens
            - Math.Max(0, request.ReserveTokens)
            - Math.Max(0, request.OutputReserveTokens));
        var threshold = Math.Max(0.3, Math.Min(request.Threshold, 1.0));
        return (int)(budget * threshold);
    }

    private static int Estimate(
        AgentCompactionRequest request,
        IReadOnlyList<AgentMessage> messages,
        bool afterCompaction = false)
    {
        if (afterCompaction && request.PostEstimator is not null)
            return request.PostEstimator(messages);

        return request.Estimator is not null
            ? request.Estimator(messages)
            : AgentTokenEstimator.Estimate(messages);
    }

    private static AgentCompactionResult Unchanged(
        List<AgentMessage> messages,
        int tokens) =>
        new()
        {
            Messages = messages,
            TokensBefore = tokens,
            TokensAfter = tokens
        };

    private static AgentCompactionResult NothingToSummarize(
        List<AgentMessage> messages,
        int tokensBefore,
        int tokensAfter,
        int maskedCount,
        IReadOnlyList<string> skillPacksToResend)
    {
        SmartConLogger.Warn(
            "Agent context: nothing safe to summarize (" + messages.Count +
            " message(s), " + tokensAfter + " tokens) [Action: the turn continues with " +
            "the current messages; open a new chat or narrow the request]");

        return new AgentCompactionResult
        {
            Messages = messages,
            MaskedCount = maskedCount,
            DroppedCount = 0,
            TokensBefore = tokensBefore,
            TokensAfter = tokensAfter,
            Fallback = true,
            FallbackReason = "nothing_to_summarize",
            SkillPacksToResend = skillPacksToResend
        };
    }

    private static (List<AgentMessage> Messages, int Dropped) HardShrink(
        IReadOnlyList<AgentMessage> messages,
        AgentCompactionRequest? request)
    {
        var keep = Math.Max(1, request?.HardShrinkMessages ?? 4);
        var cut = AlignCut(messages, Math.Max(0, messages.Count - keep));
        if (cut <= 0)
            return (messages.ToList(), 0);

        // The checkpoint is a system message at the head of the working list.
        // A hard shrink must never drop it: those turns already lost the raw
        // prefix and the checkpoint is the only surviving summary.
        var preserved = new List<AgentMessage>();
        for (var index = 0; index < cut; index++)
        {
            if (messages[index].Role == AgentRole.System)
                preserved.Add(messages[index]);
        }

        preserved.AddRange(messages.Skip(cut));
        return (preserved, cut);
    }

    /// <summary>
    /// Chooses the first message of the verbatim tail. The tail is bounded by
    /// token budget when <see cref="AgentCompactionRequest.KeepRecentTokens"/> is
    /// set and by message count otherwise; the result never starts with an
    /// orphan tool result.
    /// </summary>
    private static int ComputeCut(
        IReadOnlyList<AgentMessage> messages,
        AgentCompactionRequest request)
    {
        var minKeep = Math.Max(1, request.KeepRecentMessages);
        var start = Math.Max(0, messages.Count - minKeep);

        if (request.KeepRecentTokens > 0)
        {
            var tokens = 0;
            var tokenStart = messages.Count;
            for (var index = messages.Count - 1; index >= 0; index--)
            {
                var messageTokens = AgentTokenEstimator.Estimate(messages[index]);
                var kept = messages.Count - index;
                if (kept > minKeep && tokens + messageTokens > request.KeepRecentTokens)
                    break;

                tokens += messageTokens;
                tokenStart = index;
            }

            start = Math.Min(start, tokenStart);
        }

        return AlignCut(messages, start);
    }

    private static int AlignCut(IReadOnlyList<AgentMessage> messages, int cut)
    {
        if (cut < 0 || cut > messages.Count)
            cut = Math.Max(0, Math.Min(cut, messages.Count));

        while (cut > 0 && cut < messages.Count && messages[cut].Role == AgentRole.Tool)
            cut--;

        return cut;
    }

    /// <summary>
    /// Prepends the checkpoint to the verbatim tail that starts at
    /// <paramref name="cut"/>.
    /// </summary>
    private static (List<AgentMessage> Messages, int Dropped) BuildCheckpoint(
        IReadOnlyList<AgentMessage> messages,
        string? summary,
        int cut)
    {
        cut = AlignCut(messages, cut);
        var tail = messages.Skip(cut).ToList();
        var result = new List<AgentMessage>(tail.Count + 1);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            result.Add(AgentMessage.System(
                "<conversation_checkpoint>\n" + summary + "\n</conversation_checkpoint>"));
        }

        result.AddRange(tail);
        return (result, cut);
    }

    private static string BuildPinnedBlock(WorkingState? state)
    {
        if (state is null)
            return string.Empty;

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Pinned (must survive compaction):");
        if (!string.IsNullOrWhiteSpace(state.Goal))
            builder.AppendLine("- Goal: " + state.Goal);
        if (state.Constraints.Count > 0)
            builder.AppendLine("- Constraints: " + string.Join("; ", state.Constraints));
        if (!string.IsNullOrWhiteSpace(state.PlanId))
            builder.AppendLine("- Plan digest: " + state.PlanId);
        if (state.ChangeSetIds.Count > 0)
            builder.AppendLine("- Change sets: " + string.Join(", ", state.ChangeSetIds));
        if (state.OpenQuestions.Count > 0)
            builder.AppendLine("- Open questions: " + string.Join("; ", state.OpenQuestions));
        return builder.ToString().TrimEnd();
    }

    private async Task<(AgentCompactionRecord? Record, int ToRowId)> PersistCheckpointAsync(
        AgentCompactionRequest request,
        IReadOnlyList<AgentMessage> coveredPrefix,
        string summary,
        int tokensBefore,
        int tokensAfter,
        CancellationToken ct)
    {
        var indexedPrefix = coveredPrefix
            .Where(message => message.SessionIndex > 0)
            .ToList();

        int toRowId;
        string sourcePrefixHash;

        if (indexedPrefix.Count > 0)
        {
            toRowId = indexedPrefix[indexedPrefix.Count - 1].SessionIndex;
            if (request.RawHistory is not null)
            {
                var rawSlice = RawSlice(request, toRowId);
                if (rawSlice.Count == 0)
                    return (null, request.BaseRowId);

                sourcePrefixHash = AgentCompactionHash.Compute(rawSlice);
            }
            else
            {
                sourcePrefixHash = AgentCompactionHash.Compute(indexedPrefix);
            }
        }
        else if (request.RawHistory is not null)
        {
            // Nothing persisted was covered: a checkpoint would never validate.
            return (null, request.BaseRowId);
        }
        else
        {
            // Synthetic/test histories have no persisted ordinals: anchor them
            // after BaseRowId and hash the same way a store would.
            if (coveredPrefix.Count == 0
                || coveredPrefix.All(message => message.Role == AgentRole.System))
            {
                return (null, request.BaseRowId);
            }

            toRowId = request.BaseRowId + coveredPrefix.Count;
            var anchored = new List<AgentMessage>(coveredPrefix.Count);
            for (var index = 0; index < coveredPrefix.Count; index++)
            {
                anchored.Add(coveredPrefix[index]
                    .WithSessionIndex(request.BaseRowId + index + 1));
            }

            sourcePrefixHash = AgentCompactionHash.Compute(anchored);
        }

        if (toRowId <= request.BaseRowId)
            return (null, request.BaseRowId);

        if (_store is null || request.SessionId == Guid.Empty)
            return (null, toRowId);

        var record = new AgentCompactionRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = request.SessionId,
            FromRowId = request.BaseRowId + 1,
            ToRowId = toRowId,
            Summary = summary,
            TokensBefore = tokensBefore,
            TokensAfter = tokensAfter,
            SourcePrefixHash = sourcePrefixHash,
            Model = request.SummaryModel,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            await _store.SaveAsync(record, ct).ConfigureAwait(false);
            return (record, toRowId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent context: failed to persist the checkpoint: " + ex.GetType().Name +
                ": " + ex.Message +
                " [Action: the turn continues; the checkpoint will not survive a restart]");
            return (null, toRowId);
        }
    }

    private static List<AgentMessage> RawSlice(
        AgentCompactionRequest request,
        int toRowId)
    {
        var raw = request.RawHistory;
        if (raw is null)
            return new List<AgentMessage>();

        var from = Math.Max(0, request.BaseRowId);
        var to = Math.Min(toRowId, raw.Count);
        if (to <= from)
            return new List<AgentMessage>();

        var slice = new List<AgentMessage>(to - from);
        for (var index = from; index < to; index++)
            slice.Add(raw[index]);
        return slice;
    }
}
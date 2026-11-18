using System.Security.Cryptography;
using System.Text;
using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services;

/// <summary>
/// Context ladder (runbook H1-15/H1-16). Step 1 masks old tool results with a
/// digest that keeps the result handle; step 2 replaces old history with a
/// nine-section checkpoint; step 3 hard-shrinks to the recent tail. Failures of
/// the summarizer fall back to masking, and three consecutive failures disable
/// the summary step for this instance (circuit breaker).
/// </summary>
public sealed partial class AgentContextCompactor : IAgentContextCompactor
{
    private const int CircuitBreakerThreshold = 3;

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

    public async Task<AgentCompactionResult> CompactAsync(
        AgentCompactionRequest request,
        CancellationToken ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var messages = request.Messages.ToList();
        var tokensBefore = Estimate(request, messages);
        var limit = ComputeLimit(request);
        if (tokensBefore <= limit)
            return Unchanged(messages, tokensBefore);

        var masked = MaskOldToolResults(
            messages, request.KeepRecentToolResults, out var maskedCount);
        var maskedTokens = Estimate(request, masked);
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
                TokensAfter = maskedTokens
            };
        }

        if (_consecutiveFailures < CircuitBreakerThreshold)
        {
            try
            {
                var summary = await SummarizeCompactionAsync(masked, request, ct)
                    .ConfigureAwait(false);
                _consecutiveFailures = 0;

                var pinned = BuildPinnedBlock(request.WorkingState);
                var checkpointText = string.IsNullOrWhiteSpace(pinned)
                    ? summary
                    : summary + "\n\n" + pinned;
                var (checkpointed, dropped) = BuildCheckpoint(
                    masked, checkpointText, request.KeepRecentMessages);
                var checkpointTokens = Estimate(request, checkpointed);

                if (checkpointTokens > limit)
                {
                    var shrunk = BuildCheckpoint(
                        masked, checkpointText, request.HardShrinkMessages);
                    checkpointTokens = Estimate(request, shrunk.Messages);
                    dropped += shrunk.Dropped;
                    checkpointed = shrunk.Messages;
                }

                var record = await PersistCheckpointAsync(
                        request, checkpointText, tokensBefore, checkpointTokens, ct)
                    .ConfigureAwait(false);

                SmartConLogger.Info(
                    "Agent context: checkpoint summary, dropped=" + dropped +
                    ", masked=" + maskedCount + ", " + tokensBefore + " -> " +
                    checkpointTokens + " tokens (limit " + limit + ")");

                return new AgentCompactionResult
                {
                    Messages = checkpointed,
                    Summary = checkpointText,
                    MaskedCount = maskedCount,
                    DroppedCount = dropped,
                    TokensBefore = tokensBefore,
                    TokensAfter = checkpointTokens,
                    Summarized = true,
                    Record = record
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

        var hardShrink = BuildCheckpoint(masked, null, request.HardShrinkMessages);
        var fallbackTokens = Estimate(request, hardShrink.Messages);
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
            FallbackReason = "summary_unavailable"
        };
    }

    private static int ComputeLimit(AgentCompactionRequest request)
    {
        var budget = Math.Max(1000, request.ContextBudgetTokens);
        var threshold = Math.Max(0.3, Math.Min(request.Threshold, 0.95));
        return (int)(budget * threshold);
    }

    private static int Estimate(AgentCompactionRequest request, IReadOnlyList<AgentMessage> messages) =>
        request.Estimator is not null
            ? request.Estimator(messages)
            : AgentTokenEstimator.Estimate(messages);

    private static AgentCompactionResult Unchanged(List<AgentMessage> messages, int tokens) =>
        new()
        {
            Messages = messages,
            TokensBefore = tokens,
            TokensAfter = tokens
        };

    /// <summary>
    /// Keeps the newest <paramref name="keepRecent"/> messages and never starts
    /// the tail with an orphan tool result: its assistant call is pulled in.
    /// </summary>
    private static (List<AgentMessage> Messages, int Dropped) BuildCheckpoint(
        IReadOnlyList<AgentMessage> messages,
        string? summary,
        int keepRecent)
    {
        var start = Math.Max(0, messages.Count - Math.Max(1, keepRecent));
        while (start > 0 && messages[start].Role == AgentRole.Tool)
            start--;

        var tail = messages.Skip(start).ToList();
        var result = new List<AgentMessage>(tail.Count + 1);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            result.Add(AgentMessage.System(
                "<conversation_checkpoint>\n" + summary + "\n</conversation_checkpoint>"));
        }

        result.AddRange(tail);
        return (result, start);
    }

    private static string BuildPinnedBlock(WorkingState? state)
    {
        if (state is null)
            return string.Empty;

        var builder = new StringBuilder();
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

    private async Task<AgentCompactionRecord?> PersistCheckpointAsync(
        AgentCompactionRequest request,
        string summary,
        int tokensBefore,
        int tokensAfter,
        CancellationToken ct)
    {
        if (_store is null || request.SessionId == Guid.Empty)
            return null;

        var record = new AgentCompactionRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = request.SessionId,
            FromRowId = 1,
            ToRowId = Math.Max(0, request.CoveredMessageCount),
            Summary = summary,
            TokensBefore = tokensBefore,
            TokensAfter = tokensAfter,
            SourcePrefixHash = HashPrefix(summary),
            Model = request.SummaryModel,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            await _store.SaveAsync(record, ct).ConfigureAwait(false);
            return record;
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
            return null;
        }
    }

    private static string HashPrefix(string value)
    {
        var text = value.Length <= 2000 ? value : value.Substring(0, 2000);
        using var sha = SHA256.Create();
        return BitConverter
            .ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)))
            .Replace("-", string.Empty)
            .ToLowerInvariant();
    }
}

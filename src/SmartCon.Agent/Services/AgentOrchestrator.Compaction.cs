using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services;

public sealed partial class AgentOrchestrator
{
    /// <summary>Masking below this saving is not worth invalidating the prompt cache.</summary>
    private const int ProjectionMinMaskSavingsTokens = 20_000;

    private async Task<AgentCompactionRecord?> LoadCheckpointAsync(
        Guid conversationId,
        CancellationToken ct)
    {
        if (_compactionStore is null)
            return null;

        try
        {
            return await _compactionStore
                .GetLatestAsync(conversationId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent context: failed to load the checkpoint: " + ex.GetType().Name +
                ": " + ex.Message +
                " [Action: the turn starts from the persisted messages instead]");
            return null;
        }
    }

    /// <summary>
    /// Turn-start projection: old tool results become recoverable digests even
    /// when the raw history is below the trigger. Without it every new turn
    /// re-sends the full unmasked history and the context jumps back to the
    /// trigger.
    /// </summary>
    private async Task<AgentCompactionResult> ProjectHistoryAsync(
        List<AgentMessage> working,
        WorkingState? workingState,
        Guid conversationId,
        int baseRowId,
        IReadOnlyList<AgentMessage> rawHistory,
        Func<IReadOnlyList<AgentMessage>, int> estimator,
        Func<IReadOnlyList<AgentMessage>, int> postEstimator,
        CancellationToken ct)
    {
        if (_contextCompactor is null)
            return new AgentCompactionResult { Messages = working };

        var result = await _contextCompactor
            .ProjectAsync(
                CreateRequest(
                    working, workingState, conversationId, baseRowId, 0,
                    rawHistory, true, estimator, postEstimator),
                ct)
            .ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Runs the context ladder before a request. With no compactor injected (unit
    /// tests) it degrades to digest masking, which is the first ladder step.
    /// </summary>
    private async Task<AgentCompactionResult> CompactContextAsync(
        List<AgentMessage> working,
        WorkingState? workingState,
        Guid conversationId,
        int baseRowId,
        int reserveTokens,
        IReadOnlyList<AgentMessage> rawHistory,
        bool allowCheckpoint,
        Func<IReadOnlyList<AgentMessage>, int> estimator,
        Func<IReadOnlyList<AgentMessage>, int> postEstimator,
        CancellationToken ct)
    {
        if (_contextCompactor is null)
        {
            var before = estimator(working);
            var (masked, count) = AgentHistoryCompactor.Compact(
                working,
                _settings.ContextWindowTokens,
                _settings.ContextThreshold,
                AgentHistoryCompactor.KeepRecentToolResults,
                estimator);
            var after = estimator(masked);
            return new AgentCompactionResult
            {
                Messages = masked,
                MaskedCount = count,
                TokensBefore = before,
                TokensAfter = after
            };
        }

        var result = await _contextCompactor
            .CompactAsync(
                CreateRequest(
                    working, workingState, conversationId, baseRowId, reserveTokens,
                    rawHistory, allowCheckpoint, estimator, postEstimator),
                ct)
            .ConfigureAwait(false);

        if (result.Fallback
            && result.FallbackReason != "nothing_to_summarize"
            && result.FallbackReason != "summary_disabled")
        {
            SmartConLogger.Warn(
                "Agent context: compaction fell back (" + result.FallbackReason +
                ") [Action: the turn continues with masked history; check the summary model]");
        }

        return result;
    }

    private AgentCompactionRequest CreateRequest(
        List<AgentMessage> working,
        WorkingState? workingState,
        Guid conversationId,
        int baseRowId,
        int reserveTokens,
        IReadOnlyList<AgentMessage> rawHistory,
        bool allowCheckpoint,
        Func<IReadOnlyList<AgentMessage>, int> estimator,
        Func<IReadOnlyList<AgentMessage>, int> postEstimator) =>
        new()
        {
            Messages = working,
            ContextBudgetTokens = _settings.ContextWindowTokens,
            Threshold = _settings.ContextThreshold,
            KeepRecentToolResults = 6,
            KeepRecentMessages = 8,
            KeepRecentTokens = ComputeTailTokens(_settings),
            HardShrinkMessages = 4,
            ReserveTokens = reserveTokens,
            BaseRowId = baseRowId,
            MinMaskSavingsTokens = ProjectionMinMaskSavingsTokens,
            RawHistory = rawHistory,
            AllowCheckpoint = allowCheckpoint,
            SessionId = conversationId,
            WorkingState = workingState,
            SummaryModel = string.IsNullOrWhiteSpace(_settings.SummaryModel)
                ? _settings.Model
                : _settings.SummaryModel,
            Estimator = estimator,
            PostEstimator = postEstimator
        };

    /// <summary>
    /// Verbatim tail budget: 15 % of the usable window, clamped so both a small
    /// test window and a 1M-token model get a sane tail (plan §"Compaction").
    /// </summary>
    private static int ComputeTailTokens(AgentLlmSettings settings)
    {
        var usable = Math.Max(
            0,
            settings.ContextWindowTokens - Math.Max(0, settings.MaxOutputTokens));
        var tail = (int)(usable * 0.15);
        return Math.Max(2_000, Math.Min(150_000, tail));
    }
}

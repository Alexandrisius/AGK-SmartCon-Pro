using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services;

public sealed partial class AgentOrchestrator
{
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
    /// Runs the context ladder before a request. With no compactor injected (unit
    /// tests) it degrades to digest masking, which is the first ladder step.
    /// </summary>
    private async Task<List<AgentMessage>> CompactContextAsync(
        List<AgentMessage> working,
        WorkingState? workingState,
        Guid conversationId,
        int persistedMessageCount,
        Func<IReadOnlyList<AgentMessage>, int> estimator,
        CancellationToken ct)
    {
        if (_contextCompactor is null)
        {
            var (masked, _) = AgentHistoryCompactor.Compact(
                working,
                _settings.ContextWindowTokens,
                _settings.ContextThreshold,
                AgentHistoryCompactor.KeepRecentToolResults,
                estimator);
            return masked;
        }

        var result = await _contextCompactor
            .CompactAsync(
                new AgentCompactionRequest
                {
                    Messages = working,
                    ContextBudgetTokens = _settings.ContextWindowTokens,
                    Threshold = _settings.ContextThreshold,
                    KeepRecentToolResults = 6,
                    KeepRecentMessages = 8,
                    HardShrinkMessages = 4,
                    CoveredMessageCount = persistedMessageCount,
                    SessionId = conversationId,
                    WorkingState = workingState,
                    SummaryModel = string.IsNullOrWhiteSpace(_settings.SummaryModel)
                        ? _settings.Model
                        : _settings.SummaryModel,
                    Estimator = estimator
                },
                ct)
            .ConfigureAwait(false);

        if (result.Fallback)
        {
            SmartConLogger.Warn(
                "Agent context: compaction fell back (" + result.FallbackReason +
                ") [Action: the turn continues with masked history; check the summary model]");
        }

        return result.Messages;
    }
}

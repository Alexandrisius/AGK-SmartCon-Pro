using System.Diagnostics;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services;

public sealed partial class AgentOrchestrator
{
    private async Task WriteUsageAsync(
        Guid conversationId,
        AgentUsage usage,
        string status,
        long turnStartedAt,
        string? requestId,
        int estimatedPromptTokens,
        CancellationToken ct)
    {
        if (_usageStore is null)
            return;

        var latencyMs = (long)((Stopwatch.GetTimestamp() - turnStartedAt)
            * 1000.0 / Stopwatch.Frequency);

        try
        {
            await _usageStore
                .RecordAsync(new AgentUsageRecord
                {
                    SessionId = conversationId.ToString("D"),
                    Provider = _llm.ProviderName,
                    Model = _llm.Model,
                    Usage = usage,
                    Status = status,
                    LatencyMs = latencyMs,
                    RequestId = requestId,
                    EstimatedPromptTokens = estimatedPromptTokens
                }, ct)
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
            CachedTokens = left.CachedTokens + right.CachedTokens,
            CacheWriteTokens = left.CacheWriteTokens + right.CacheWriteTokens,
            TotalTokens = left.TotalTokens + right.TotalTokens,
            CostUsd = cost
        };
    }
}

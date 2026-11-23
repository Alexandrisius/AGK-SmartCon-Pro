using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Agent.Services;

public sealed partial class AgentOrchestrator
{
    /// <summary>
    /// Runs one call and turns what the guards observed into warnings on its
    /// result: the loop-guard hint and the call-storm hints. A hint is only
    /// ever attached to a tool result: a message between a tool call and its
    /// result is not a valid conversation. Nothing here ends the turn — a host
    /// that stopped answering is reported to the model, which tells the user;
    /// only Stop and the loop guard end a turn.
    /// </summary>
    private async Task<ToolResult> ExecuteObservedToolAsync(
        LoopGuard loopGuard,
        ToolStormTracker storms,
        AgentToolCall toolCall,
        string argumentsJson,
        bool exempt,
        int round,
        CancellationToken ct)
    {
        var result = await ExecuteToolAsync(toolCall, ct).ConfigureAwait(false);

        var observed = loopGuard.ObserveResult(toolCall.Name, argumentsJson, result.Success, exempt);
        if (observed.WarnOnly && !result.Success)
        {
            SmartConLogger.Info(
                "Agent loop guard warning: tool=" + toolCall.Name +
                ", identicalErrors=" + observed.IdenticalErrors);
            if (!string.IsNullOrEmpty(observed.Hint))
                result = result.WithWarnings(new[] { observed.Hint! });
        }

        var hints = storms.Observe(_toolRegistry, toolCall.Name, argumentsJson, result, round);
        if (hints.Count > 0)
        {
            SmartConLogger.Info(
                "Agent call-storm hint: tool=" + toolCall.Name + ", round=" + round +
                ", hints=" + hints.Count);
            result = result.WithWarnings(hints);
        }

        return result;
    }
}

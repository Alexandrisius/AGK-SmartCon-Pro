using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.UI;

namespace SmartCon.Agent.Services;

public sealed partial class AgentOrchestrator
{
    /// <summary>
    /// Consecutive tool timeouts with the tool still running in the host that
    /// stop the turn (C1-01). This is no turn limit: the turn ends because the
    /// host stopped answering, not because a budget ran out.
    /// </summary>
    public const int HostTimeoutStreak = 3;

    public const string HostNotRespondingStatus = "host_not_responding";

    private readonly struct ToolOutcome
    {
        public ToolOutcome(ToolResult result, int hostTimeoutStreak, bool hostStopped)
        {
            Result = result;
            HostTimeoutStreak = hostTimeoutStreak;
            HostStopped = hostStopped;
        }

        public ToolResult Result { get; }

        public int HostTimeoutStreak { get; }

        public bool HostStopped { get; }
    }

    /// <summary>
    /// Runs one call and turns what the guards observed into warnings on its
    /// result: the loop-guard hint, the storm hints and the host-timeout
    /// streak. A hint is only ever attached to a tool result: a message between
    /// a tool call and its result is not a valid conversation.
    /// </summary>
    private async Task<ToolOutcome> ExecuteObservedToolAsync(
        LoopGuard loopGuard,
        ToolStormTracker storms,
        int hostTimeoutStreak,
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

        var streak = IsHostTimeout(result) ? hostTimeoutStreak + 1 : 0;
        var stopped = streak >= HostTimeoutStreak;
        if (stopped)
        {
            SmartConLogger.Warn(
                "Agent turn stopped: " + streak + " tool calls in a row timed out while still " +
                "running in the Revit host [Action: check Revit responsiveness (a dialog or a " +
                "long operation may block its main thread) and read the tool log]");
        }

        return new ToolOutcome(result, streak, stopped);
    }

    /// <summary>
    /// The tool did not answer within its timeout and ignored the cancellation:
    /// the host thread is busy, not this one call.
    /// </summary>
    private static bool IsHostTimeout(ToolResult result) =>
        !result.Success
        && string.Equals(result.ErrorCode, "tool_timeout", StringComparison.Ordinal)
        && result.Data.HasValue
        && result.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object
        && result.Data.Value.TryGetProperty("stillRunning", out var stillRunning)
        && stillRunning.ValueKind == System.Text.Json.JsonValueKind.True;

    /// <summary>
    /// The line the panel shows for a turn the harness stopped, and the status
    /// written to the usage record: both must name the same reason.
    /// </summary>
    private string StoppedTurnText(bool hostStopped)
    {
        if (!hostStopped)
        {
            return (LanguageManager.GetString("Agent_Status_LoopAborted")
                    ?? "Stopped: repeated tool calls were detected") + " (loop_aborted).";
        }

        return "Stopped: the Revit host is not responding — " + HostTimeoutStreak +
               " tool calls in a row timed out while still running. Tell the user that the host " +
               "is not responding and check Revit (a dialog or a long operation may be blocking " +
               "it) before retrying. (" + HostNotRespondingStatus + ")";
    }
}

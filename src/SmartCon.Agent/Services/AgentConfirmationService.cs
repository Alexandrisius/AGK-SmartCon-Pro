using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services;

/// <summary>
/// Bridges the orchestrator (background thread) with the chat panel overlay.
/// The view model subscribes to <see cref="Requested"/>, shows the dialog and
/// calls <see cref="Resolve"/>. Without a subscriber the request is rejected
/// (fail-closed).
/// </summary>
public sealed class AgentConfirmationService : IAgentConfirmationService
{
    private readonly object _gate = new();
    private TaskCompletionSource<AgentConfirmationResult>? _pending;

    public event Action<AgentConfirmationRequest>? Requested;

    /// <summary>
    /// Raised when a request is settled — resolved, cancelled or timed out — so
    /// the panel can hide an overlay that is still visible after a timeout.
    /// </summary>
    public event Action? Finished;

    public AgentConfirmationRequest? Pending { get; private set; }

    public async Task<AgentConfirmationResult> RequestAsync(
        AgentConfirmationRequest request,
        CancellationToken ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var handler = Requested;
        if (handler is null)
        {
            SmartConLogger.Warn(
                "Agent confirmation requested for '" + request.ToolName +
                "' but no UI subscriber is registered — the write is rejected " +
                "[Action: do not call write tools with dryRun=false while the Agent panel is closed]");
            return AgentConfirmationResult.Rejected;
        }

        var completion = new TaskCompletionSource<AgentConfirmationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (_pending is not null)
            {
                SmartConLogger.Warn(
                    "Agent confirmation for '" + request.ToolName + "' was rejected: another " +
                    "confirmation is already pending [" + Pending?.ToolName +
                    "] [Action: wait for the open dialog; only one write is confirmed at a time]");
                return AgentConfirmationResult.Rejected;
            }

            _pending = completion;
            Pending = request;
        }

        try
        {
            handler(request);

            using var cancellation = ct.Register(
                () => completion.TrySetResult(AgentConfirmationResult.Cancelled));
            using var timeout = new CancellationTokenSource(request.Timeout);
            using var timeoutRegistration = timeout.Token.Register(
                () => completion.TrySetResult(AgentConfirmationResult.TimedOut));

            var result = await completion.Task.ConfigureAwait(false);
            SmartConLogger.Info(
                "Agent confirmation for '" + request.ToolName + "': " + result +
                " (digest " + Digest(request.Digest) + ")");
            return result;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, completion))
                {
                    _pending = null;
                    Pending = null;
                }
            }

            Finished?.Invoke();
        }
    }

    public void Resolve(AgentConfirmationResult result)
    {
        TaskCompletionSource<AgentConfirmationResult>? pending;
        lock (_gate)
            pending = _pending;

        pending?.TrySetResult(result);
    }

    private static string Digest(string digest) =>
        string.IsNullOrEmpty(digest)
            ? "-"
            : digest.Length <= 12 ? digest : digest.Substring(0, 12);
}

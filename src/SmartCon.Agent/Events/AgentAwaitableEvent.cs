using System.Collections.Concurrent;
using System.Diagnostics;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Events;

public sealed class AgentAwaitableEvent : IAgentAwaitableEvent
{
    /// <summary>
    /// A queued action that waited longer than this means the Revit main thread
    /// was blocked; the caller will likely hit the per-tool timeout (H1-10).
    /// </summary>
    internal const int SlowQueueWarnThresholdMs = 60000;

    private readonly ConcurrentQueue<Entry> _queue = new();
    private readonly IRevitContextWriter? _contextWriter;
    private Action? _onRaise;

    public AgentAwaitableEvent(IRevitContextWriter? contextWriter = null)
    {
        _contextWriter = contextWriter;
    }

    internal int PendingCount => _queue.Count;

    public void Initialize(Action onRaise)
    {
        _onRaise = onRaise ?? throw new ArgumentNullException(nameof(onRaise));
    }

    public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
    {
        ThrowIfNull(actionWithApp);
        EnsureInitialized();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Entry(
            app => InvokeGuarded(actionWithApp, app, tcs, ct),
            Stopwatch.GetTimestamp());
        _queue.Enqueue(entry);
        RegisterCancellation(tcs, ct);
        _onRaise!.Invoke();
        return tcs.Task;
    }

    public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
    {
        ThrowIfNull(funcWithApp);
        EnsureInitialized();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Entry(
            app => InvokeGuarded(funcWithApp, app, tcs, ct),
            Stopwatch.GetTimestamp());
        _queue.Enqueue(entry);
        RegisterCancellation(tcs, ct);
        _onRaise!.Invoke();
        return tcs.Task;
    }

    public void ProcessQueue(object revitApp)
    {
        using var _scope = SmartConLogger.BeginScope("Agent", ("Method", nameof(ProcessQueue)));
        UpdateRevitContext(revitApp);
        var processed = 0;
        while (_queue.TryDequeue(out var entry))
        {
            processed++;
            var waitedMs = (Stopwatch.GetTimestamp() - entry.EnqueuedAt) * 1000.0 / Stopwatch.Frequency;
            var waitedText = waitedMs.ToString("F0");
            if (IsSlowQueueWait(waitedMs))
            {
                SmartConLogger.Warn(
                    "[AgentAwaitable] Execute start: waited=" + waitedText +
                    "ms, pending=" + _queue.Count +
                    " [Action: the Revit main thread is busy; the agent call may time out]");
            }
            else
            {
                SmartConLogger.Debug(
                    "[AgentAwaitable] Execute start: waited=" + waitedText +
                    "ms, pending=" + _queue.Count);
            }

            entry.Action(revitApp);
        }

        if (processed > 0)
            SmartConLogger.Debug("[AgentAwaitable] Drained " + processed + " action(s)");
    }

    internal static bool IsSlowQueueWait(double waitedMs) =>
        waitedMs > SlowQueueWarnThresholdMs;

    private void UpdateRevitContext(object revitApp)
    {
        if (_contextWriter is null)
            return;

        try
        {
            _contextWriter.SetContext(revitApp);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "[AgentAwaitable] Revit context update failed: " +
                ex.GetType().Name + ": " + ex.Message +
                " [Action: ensure ExternalEvent.Execute receives a UIApplication; " +
                "tools will fail with no_active_document otherwise]");
        }
    }

    private static void InvokeGuarded<T>(
        Func<object, T> func,
        object app,
        TaskCompletionSource<T> tcs,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            tcs.TrySetCanceled(ct);
            return;
        }

        try
        {
            tcs.TrySetResult(func(app));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            tcs.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                "[AgentAwaitable] Revit callback failed: " + ex.GetType().Name + ": " + ex.Message);
            tcs.TrySetException(ex);
        }
    }

    private static void InvokeGuarded(
        Action<object> action,
        object app,
        TaskCompletionSource<bool> tcs,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            tcs.TrySetCanceled(ct);
            return;
        }

        try
        {
            action(app);
            tcs.TrySetResult(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            tcs.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                "[AgentAwaitable] Revit callback failed: " + ex.GetType().Name + ": " + ex.Message);
            tcs.TrySetException(ex);
        }
    }

    private static void RegisterCancellation<T>(TaskCompletionSource<T> tcs, CancellationToken ct)
    {
        if (!ct.CanBeCanceled) return;
        ct.Register(() => tcs.TrySetCanceled(ct));
    }

    private void EnsureInitialized()
    {
        if (_onRaise is null)
        {
            throw new InvalidOperationException(
                "AgentAwaitableEvent is not initialized. Call Initialize(onRaise) at startup.");
        }
    }

    private static void ThrowIfNull(object? value)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(value);
#else
        if (value is null)
            throw new ArgumentNullException(nameof(value));
#endif
    }

    private sealed class Entry
    {
        public Entry(Action<object> action, long enqueuedAt)
        {
            Action = action;
            EnqueuedAt = enqueuedAt;
        }

        public Action<object> Action { get; }

        public long EnqueuedAt { get; }
    }
}
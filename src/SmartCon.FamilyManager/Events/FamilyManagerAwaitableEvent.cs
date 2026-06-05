using System.Collections.Concurrent;
using System.Diagnostics;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Events;

/// <summary>
/// Task-aware wrapper around a host-supplied "raise" callback that
/// lets callers <see langword="await"/> the completion of their
/// Revit-API callbacks. This is the canonical pattern for async
/// coordination in Revit plugins: <c>TaskCompletionSource</c> +
/// <c>ExternalEvent</c> + <c>TaskCreationOptions.RunContinuationsAsynchronously</c>.
/// </summary>
/// <remarks>
/// <para><b>Why this lives here, not in <c>SmartCon.Revit</c>.</b>
/// The class does not implement <c>Autodesk.Revit.UI.IExternalEventHandler</c>
/// (a sealed runtime-only type from <c>RevitAPIUI</c>) so it stays
/// testable without a live Revit. The host's <see cref="IExternalEventHandler"/>
/// adapter (see <c>RevitFamilyManagerAwaitableEvent</c>) simply
/// forwards its <c>Execute(UIApplication)</c> to
/// <see cref="ProcessQueue(object)"/>.</para>
/// <para><b>Why this exists.</b> <c>ExternalEvent.Raise</c> is
/// fire-and-forget: it enqueues an action that the Revit message loop
/// will eventually dispatch. Without a completion signal, callers
/// cannot sequence dependent steps (e.g. <i>extract attributes from
/// .rvt</i> → <i>save extraction result</i> → <i>cleanup temp files</i>)
/// and race conditions emerge where cleanup runs before extraction
/// completes, silently losing data.</para>
/// <para><b>How it works.</b> Each <see cref="RaiseAsync(Action{object}, CancellationToken)"/>
/// call enqueues an <see cref="Entry"/> wrapping the action and a
/// <see cref="TaskCompletionSource{TResult}"/> of <c>bool</c> (or
/// <c>T</c> for <see cref="RaiseAsync{T}(Func{object, T}, CancellationToken)"/>).
/// When <see cref="ProcessQueue(object)"/> drains the queue, it
/// invokes the action, captures exceptions / cancellation, and
/// completes the matching <see cref="TaskCompletionSource{TResult}"/>
/// with the appropriate state.</para>
/// <para><b>FIFO ordering.</b> <see cref="ConcurrentQueue{T}"/> is
/// used so that multiple enqueueers (UI thread, WPF dispatcher,
/// thread pool callbacks) can safely enqueue actions from any
/// thread; <see cref="ProcessQueue(object)"/> drains the queue in
/// strict FIFO order during a single Revit message-loop pass.</para>
/// <para><b>Why <c>RunContinuationsAsynchronously</c>.</b> Without
/// this flag, an <see langword="await"/> continuation would run on
/// the Revit UI thread that completed the task, blocking the message
/// loop. With it, the continuation is scheduled on the thread pool
/// (or captured context), keeping the UI thread responsive.</para>
/// </remarks>
public sealed class FamilyManagerAwaitableEvent : IFamilyManagerAwaitableEvent
{
    private readonly IRevitContextWriter _contextWriter;
    private readonly IWindowFocusService? _windowFocusService;
    private readonly ConcurrentQueue<Entry> _queue = new();
    private Action? _onRaise;

    /// <summary>
    /// Snapshot of pending entries. Exposed for diagnostics and tests.
    /// </summary>
    internal int PendingCount => _queue.Count;

    /// <summary>
    /// Creates the handler. The handler cannot be used until
    /// <see cref="Initialize"/> is called.
    /// </summary>
    /// <param name="contextWriter">Writer used to update the shared
    ///     Revit context on each <see cref="Execute(UIApplication)"/>
    ///     call. Required.</param>
    /// <param name="windowFocusService">Optional. Restores focus and
    ///     refreshes WPF render after operations that may have
    ///     triggered a native Revit dialog (e.g. family upgrade).</param>
    public FamilyManagerAwaitableEvent(
        IRevitContextWriter contextWriter,
        IWindowFocusService? windowFocusService = null)
    {
        _contextWriter = contextWriter ?? throw new ArgumentNullException(nameof(contextWriter));
        _windowFocusService = windowFocusService;
    }

    /// <summary>
    /// Binds the handler to the host's "raise" signal. In production
    /// this is <c>() => revitEvent.Raise()</c>; in tests it is a
    /// plain <see cref="Action"/> that records the call. Decoupling
    /// from <c>Autodesk.Revit.UI.ExternalEvent</c> (a sealed runtime
    /// type) makes the handler unit-testable without a live Revit.
    /// </summary>
    /// <param name="onRaise">Action invoked after each enqueue to
    ///     ask the host to schedule <see cref="Execute(UIApplication)"/>.</param>
    public void Initialize(Action onRaise)
    {
        _onRaise = onRaise ?? throw new ArgumentNullException(nameof(onRaise));
        SmartConLogger.Debug(
            "[AwaitableEvent] Initialized: handler bound to host raise signal");
    }

    /// <inheritdoc />
    public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
    {
        ThrowIfNull(actionWithApp);
        EnsureInitialized();

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueuedAt = Stopwatch.GetTimestamp();
        var entry = new Entry(
            WrapForCancellation(actionWithApp, tcs, ct),
            enqueuedAt);
        _queue.Enqueue(entry);

        SmartConLogger.Debug(
            $"[AwaitableEvent] RaiseAsync: enqueued (pending={_queue.Count})");
        _onRaise!.Invoke();
        return tcs.Task;
    }

    /// <inheritdoc />
    public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
    {
        ThrowIfNull(funcWithApp);
        EnsureInitialized();

        var tcs = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueuedAt = Stopwatch.GetTimestamp();
        var entry = new Entry(
            obj =>
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                    return;
                }
                try
                {
                    var result = funcWithApp(obj);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            enqueuedAt);
        _queue.Enqueue(entry);

        SmartConLogger.Debug(
            $"[AwaitableEvent] RaiseAsync<{typeof(T).Name}>: enqueued (pending={_queue.Count})");
        _onRaise!.Invoke();
        return tcs.Task;
    }

    /// <summary>
    /// Invoked by the host's <c>IExternalEventHandler</c> adapter on
    /// the UI thread when a queued action is ready to be processed.
    /// Drains the entire queue in FIFO order. Also exposed
    /// <c>internal</c> for unit tests that drive the queue without
    /// a real Revit instance.
    /// </summary>
    public void ProcessQueue(object revitUIApplication)
    {
        _contextWriter.SetContext(revitUIApplication);

        var processed = 0;
        while (_queue.TryDequeue(out var entry))
        {
            processed++;
            var waitedMs = (Stopwatch.GetTimestamp() - entry.EnqueuedAt) * 1000.0 / Stopwatch.Frequency;
            SmartConLogger.Debug(
                $"[AwaitableEvent] Execute[{processed}]: start, " +
                $"waited={waitedMs:F0}ms (pending={_queue.Count})");
            // All enqueued Action trampolines (WrapForCancellation and
            // the generic-typed closure) catch their own exceptions and
            // route them to the matching TaskCompletionSource. If an
            // exception propagates out of entry.Action it is a bug in
            // our code and we let Revit / the test surface see it.
            entry.Action(revitUIApplication);
            SmartConLogger.Debug(
                $"[AwaitableEvent] Execute[{processed}]: completed (pending={_queue.Count})");
        }

        if (processed == 0)
        {
            SmartConLogger.Debug(
                "[AwaitableEvent] Execute: drained empty queue (deferred cycle)");
        }
        else
        {
            SmartConLogger.Debug(
                $"[AwaitableEvent] Execute: drained {processed} action(s)");
        }

        _windowFocusService?.RestoreFocusAndRefreshUI();
    }

    private void EnsureInitialized()
    {
        if (_onRaise is null)
        {
            throw new InvalidOperationException(
                "FamilyManagerAwaitableEvent is not initialized. " +
                "Call Initialize(onRaise) at startup before RaiseAsync.");
        }
    }

    /// <summary>
    /// Null-check helper. Uses the .NET 8+ built-in where available
    /// (CA1510) and falls back to a manual throw for net48.
    /// </summary>
    private static void ThrowIfNull(object? value)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(value);
#else
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }
#endif
    }

    /// <summary>
    /// Wraps a void-returning <see cref="Action{Object}"/> so that it
    /// honours the <see cref="CancellationToken"/> and routes
    /// completion / failure to the <see cref="TaskCompletionSource{TResult}"/>.
    /// </summary>
    private static Action<object> WrapForCancellation(
        Action<object> action,
        TaskCompletionSource<bool> tcs,
        CancellationToken ct)
    {
        return obj =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }
            try
            {
                action(obj);
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        };
    }

    /// <summary>
    /// Internal queue entry. Carries the trampoline action (which
    /// knows how to complete the right kind of <c>TaskCompletionSource</c>)
    /// plus the enqueue timestamp used for wait-time diagnostics.
    /// </summary>
    private sealed class Entry
    {
        public Action<object> Action { get; }
        public long EnqueuedAt { get; }

        public Entry(Action<object> action, long enqueuedAt)
        {
            Action = action;
            EnqueuedAt = enqueuedAt;
        }
    }
}

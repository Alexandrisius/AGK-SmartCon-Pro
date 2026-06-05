namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Awaitable wrapper around <c>Autodesk.Revit.UI.ExternalEvent</c> that
/// exposes a <see cref="Task"/> (or <see cref="Task{TResult}"/>) which
/// completes only after the queued action has finished executing on the
/// Revit UI thread. This is the canonical pattern recommended by
/// Jeremy Tammik and the Autodesk Developer Documentation for
/// coordinating async/await code with the Revit API.
/// </summary>
/// <remarks>
/// <para>Design contract:</para>
/// <list type="bullet">
///   <item><description>All <see cref="RaiseAsync(Action{object}, CancellationToken)"/>
///     and <see cref="RaiseAsync{T}(Func{object, T}, CancellationToken)"/>
///     calls invoked between two <c>ExternalEvent.Execute</c> cycles are
///     processed in FIFO order on the UI thread.</description></item>
///   <item><description>The returned <see cref="Task"/> completes
///     <b>only after</b> the queued action has finished executing (success,
///     exception, or cancellation). Continuations are scheduled via
///     <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>
///     to avoid running continuations on the Revit UI thread.</description></item>
///   <item><description>Exceptions raised inside the action are
///     propagated to the returned <see cref="Task"/> via
///     <see cref="TaskCompletionSource{TResult}.TrySetException"/>.</description></item>
///   <item><description><see cref="CancellationToken"/> cancels the
///     wait. Cancelling after the action has started does NOT abort the
///     action; it only short-circuits the awaiter.</description></item>
///   <item><description>Calling <c>RaiseAsync</c> before
///     <c>Initialize</c> throws <see cref="InvalidOperationException"/>.</description></item>
/// </list>
/// <para>Reentrancy warning: raising a new event from inside a queued
/// action will enqueue a new entry that will be processed in the
/// <i>next</i> <c>Execute</c> cycle, not the current one.</para>
/// </remarks>
public interface IFamilyManagerAwaitableEvent
{
    /// <summary>
    /// Queues an action to run on the Revit UI thread and returns a
    /// <see cref="Task"/> that completes when the action has finished.
    /// </summary>
    /// <param name="actionWithApp">Callback receiving the
    ///     <c>UIApplication</c> as <see cref="object"/> to avoid
    ///     <c>RevitAPIUI</c> dependency in <c>SmartCon.Core</c>
    ///     (invariant I-09).</param>
    /// <param name="ct">Cancellation token. Cancelling transitions the
    ///     returned <see cref="Task"/> to <see cref="TaskStatus.Canceled"/>
    ///     but does not abort the action if it has already started.</param>
    /// <returns>A <see cref="Task"/> that completes after the action
    ///     finishes (or is cancelled).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="actionWithApp"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Handler has not been initialized with a Revit <c>ExternalEvent</c>.</exception>
    Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default);

    /// <summary>
    /// Queues a function to run on the Revit UI thread and returns a
    /// <see cref="Task{TResult}"/> that resolves to the function's
    /// return value once it has finished.
    /// </summary>
    /// <param name="funcWithApp">Callback receiving the
    ///     <c>UIApplication</c> as <see cref="object"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Task{TResult}"/> resolving to
    ///     <typeparamref name="T"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="funcWithApp"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Handler has not been initialized.</exception>
    Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default);

    /// <summary>
    /// Drains the queue and completes every pending
    /// <see cref="TaskCompletionSource{TResult}"/>. In production this
    /// is invoked by the host's <c>IExternalEventHandler</c> adapter
    /// from inside <c>Execute(UIApplication)</c>. In tests it is called
    /// directly with a plain <see cref="object"/> as the
    /// <c>UIApplication</c> surrogate.
    /// </summary>
    /// <param name="revitUIApplication">The <c>UIApplication</c> cast
    ///     as <see cref="object"/>. Implementations pass it on to
    ///     queued callbacks.</param>
    void ProcessQueue(object revitUIApplication);
}

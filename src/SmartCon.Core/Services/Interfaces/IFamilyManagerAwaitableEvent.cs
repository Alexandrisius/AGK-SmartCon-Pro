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
    Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default);
    Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default);

    /// <summary>
    /// Async overload of <see cref="RaiseAsync(Action{Object}, CancellationToken)"/>.
    /// Renamed to <c>RaiseAsyncTask</c> to avoid C# overload-resolution
    /// ambiguity: a statement lambda <c>_ => { ... }</c> could otherwise
    /// be picked up as <see cref="Func{Object, Task}"/> (implicit async
    /// conversion), silently changing exception-propagation semantics.
    /// </summary>
    Task RaiseAsyncTask(Func<object, Task> asyncActionWithApp, CancellationToken ct = default);

    void ProcessQueue(object revitApp);
    void Initialize(Action onRaise);
}

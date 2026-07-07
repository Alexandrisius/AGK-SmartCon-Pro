using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Tests.TestDoubles;

/// <summary>
/// In-process replacement for <see cref="IFamilyManagerAwaitableEvent"/> that
/// invokes the supplied <c>Func</c>/<c>Action</c> synchronously and returns
/// the result on the same thread. This is sufficient for unit-testing code
/// that uses <see cref="IFamilyManagerAwaitableEvent"/> purely to marshal to
/// the Revit main thread — for tests, the marshalling is implicit because
/// the callback is already called on the test thread.
/// <para>
///     Bypasses Castle DynamicProxy: Moq cannot create a proxy for the
///     interface because <c>RaiseAsync&lt;T&gt;(Func&lt;object, T&gt;)</c> has
///     a generic type parameter that can be a nullable value type
///     (e.g. <c>FamilyVersion?</c>), and Castle refuses to generate such
///     proxies. A hand-written fake avoids the issue.
/// </para>
/// </summary>
public sealed class FakeFamilyManagerAwaitableEvent : IFamilyManagerAwaitableEvent
{
    public int RaiseCallCount { get; private set; }
    public int RaiseAsyncCallCount { get; private set; }
    public int RaiseAsyncTaskCallCount { get; private set; }

    public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
    {
        RaiseCallCount++;
        ct.ThrowIfCancellationRequested();
        actionWithApp(null!);
        return Task.CompletedTask;
    }

    public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
    {
        RaiseAsyncCallCount++;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(funcWithApp(null!));
    }

    public Task RaiseAsyncTask(Func<object, Task> asyncActionWithApp, CancellationToken ct = default)
    {
        RaiseAsyncTaskCallCount++;
        ct.ThrowIfCancellationRequested();
        return asyncActionWithApp(null!);
    }

    public void ProcessQueue(object revitApp)
    {
    }

    public void Initialize(Action onRaise)
    {
    }
}

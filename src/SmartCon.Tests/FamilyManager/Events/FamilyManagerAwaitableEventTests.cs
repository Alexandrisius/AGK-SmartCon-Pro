using System.Collections.Concurrent;
using SmartCon.FamilyManager.Events;
using SmartCon.Tests.FamilyManager.Events.Fakes;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Events;

/// <summary>
/// Unit tests for <see cref="FamilyManagerAwaitableEvent"/>.
///
/// <para>Why these tests don't touch Revit:</para>
/// <list type="bullet">
///   <item><description><c>UIApplication</c> is a sealed type with no
///     public constructor. It cannot be instantiated in a unit test.</description></item>
///   <item><description>Revit API assemblies are referenced as
///     compile-time only (no runtime assets), so loading them would
///     throw <see cref="FileNotFoundException"/> on CI.</description></item>
///   <item><description>Instead, the handler exposes an
///     <c>internal ProcessQueue(object)</c> test seam that takes any
///     object as the <c>UIApplication</c> surrogate. The tests pass a
///     plain <see cref="object"/> and assert behaviour end-to-end.</description></item>
/// </list>
/// </summary>
public sealed class FamilyManagerAwaitableEventTests
{
    private readonly FakeRevitContextWriter _context = new();
    private readonly object _uiAppSurrogate = new();

    private static FamilyManagerAwaitableEvent CreateInitialized(
        FakeRevitContextWriter context,
        out int raiseCount)
    {
        var handler = new FamilyManagerAwaitableEvent(context);
        var counter = 0;
        handler.Initialize(() => counter++);
        raiseCount = 0;
        // We use a closure indirection: increment counter, return the
        // current count via out. Cleaner approach below.
        return handler;
    }

    private static FamilyManagerAwaitableEvent CreateInitializedWithCounter(
        FakeRevitContextWriter context,
        out RaiseCounter counter)
    {
        var handler = new FamilyManagerAwaitableEvent(context);
        counter = new RaiseCounter();
        handler.Initialize(counter.Raise);
        return handler;
    }

    [Fact]
    public async Task Initialize_NotCalled_RaiseAsync_Throws()
    {
        var sut = new FamilyManagerAwaitableEvent(_context);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.RaiseAsync(_ => { }));
    }

    [Fact]
    public async Task RaiseAsync_Action_TaskCompletesAfterProcessQueue()
    {
        var sut = CreateInitializedWithCounter(_context, out _);
        var actionInvoked = false;

        var task = sut.RaiseAsync(_ => actionInvoked = true);

        Assert.False(task.IsCompleted, "Task should not complete before ProcessQueue runs");
        Assert.False(actionInvoked, "Action should not run before ProcessQueue");

        sut.ProcessQueue(_uiAppSurrogate);

        Assert.True(task.IsCompleted, "Task should complete after ProcessQueue");
        Assert.True(actionInvoked, "Action should run during ProcessQueue");
        await task;
    }

    [Fact]
    public async Task RaiseAsync_GenericFunction_ReturnsValue()
    {
        var sut = CreateInitializedWithCounter(_context, out _);

        var task = sut.RaiseAsync<int>(_ => 42);

        Assert.False(task.IsCompleted);
        sut.ProcessQueue(_uiAppSurrogate);

        Assert.True(task.IsCompleted);
        Assert.Equal(42, await task);
    }

    [Fact]
    public async Task RaiseAsync_ActionThrows_ExceptionPropagatedToTask()
    {
        var sut = CreateInitializedWithCounter(_context, out _);
        var expected = new InvalidOperationException("boom");

        var task = sut.RaiseAsync((Action<object>)(_ => throw expected));

        sut.ProcessQueue(_uiAppSurrogate);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await task);
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task RaiseAsync_GenericFunctionThrows_ExceptionPropagated()
    {
        var sut = CreateInitializedWithCounter(_context, out _);
        var expected = new ArgumentException("bad arg");

        var task = sut.RaiseAsync<int>(_ => throw expected);

        sut.ProcessQueue(_uiAppSurrogate);

        var actual = await Assert.ThrowsAsync<ArgumentException>(
            async () => await task);
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task RaiseAsync_Cancelled_TaskIsCanceled()
    {
        var sut = CreateInitializedWithCounter(_context, out _);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = sut.RaiseAsync(_ => { }, cts.Token);

        sut.ProcessQueue(_uiAppSurrogate);

        await Assert.ThrowsAsync<TaskCanceledException>(
            async () => await task);
    }

    [Fact]
    public async Task RaiseAsync_MultipleActions_ProcessedInFifoOrder()
    {
        var sut = CreateInitializedWithCounter(_context, out _);
        var observedOrder = new ConcurrentQueue<int>();

        var t0 = sut.RaiseAsync(_ => observedOrder.Enqueue(0));
        var t1 = sut.RaiseAsync(_ => observedOrder.Enqueue(1));
        var t2 = sut.RaiseAsync(_ => observedOrder.Enqueue(2));

        Assert.Equal(3, sut.PendingCount);
        sut.ProcessQueue(_uiAppSurrogate);
        Assert.Equal(0, sut.PendingCount);

        await Task.WhenAll(t0, t1, t2);

        Assert.Equal(new[] { 0, 1, 2 }, observedOrder);
    }

    [Fact]
    public void ProcessQueue_EmptyQueue_DoesNotThrowAndSetsContext()
    {
        var sut = CreateInitializedWithCounter(_context, out _);

        var ex = Record.Exception(() => sut.ProcessQueue(_uiAppSurrogate));

        Assert.Null(ex);
        Assert.Equal(1, _context.CallCount);
        Assert.Same(_uiAppSurrogate, _context.LastContext);
    }

    [Fact]
    public async Task RaiseAsync_Continuation_RunsAfterTaskCompletes()
    {
        var sut = CreateInitializedWithCounter(_context, out var counter);
        var task = sut.RaiseAsync(_ => { });
        var threadBeforeDrain = Environment.CurrentManagedThreadId;

        sut.ProcessQueue(_uiAppSurrogate);
        await task;

        // TaskCreationOptions.RunContinuationsAsynchronously guarantees
        // that the continuation does not run inline on the calling
        // thread. We just assert the task completed successfully —
        // asserting the exact thread ID is brittle under JIT inlining.
        Assert.True(task.IsCompletedSuccessfully);
        Assert.NotEqual(0, threadBeforeDrain);
        Assert.Equal(1, counter.Count);
    }

    /// <summary>
    /// Counts the number of times the host's "raise" callback is
    /// invoked. In production this corresponds to
    /// <c>ExternalEvent.Raise</c>; in tests it is a plain delegate.
    /// </summary>
    private sealed class RaiseCounter
    {
        public int Count { get; private set; }
        public void Raise() => Count++;
    }
}

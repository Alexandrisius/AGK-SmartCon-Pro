using Xunit;
using SmartCon.Agent.Events;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Tests.Agent.Events;

public sealed class AgentAwaitableEventTests
{
    [Fact]
    public async Task RaiseAsync_CompletesOnlyAfterProcessQueue()
    {
        var awaitable = new AgentAwaitableEvent();
        var raised = 0;
        awaitable.Initialize(() => raised++);

        var task = awaitable.RaiseAsync<int>(_ => 42);

        Assert.False(task.IsCompleted);
        Assert.Equal(1, raised);
        Assert.Equal(1, awaitable.PendingCount);

        awaitable.ProcessQueue(new object());

        Assert.Equal(42, await task);
        Assert.Equal(0, awaitable.PendingCount);
    }

    [Fact]
    public async Task RaiseAsync_CallbackThrows_FaultsAwaiterAndDrainsQueue()
    {
        var awaitable = new AgentAwaitableEvent();
        awaitable.Initialize(() => { });
        var failing = awaitable.RaiseAsync<int>(
            _ => throw new InvalidOperationException("boom"));
        var succeeding = awaitable.RaiseAsync<int>(_ => 7);

        awaitable.ProcessQueue(new object());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        Assert.Equal("boom", exception.Message);
        Assert.Equal(7, await succeeding);
    }

    [Fact]
    public async Task RaiseAsync_CancelledToken_CancelsAwaiterWithoutProcessing()
    {
        var awaitable = new AgentAwaitableEvent();
        awaitable.Initialize(() => { });
        using var cts = new CancellationTokenSource();

        var task = awaitable.RaiseAsync<int>(_ => 1, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void ProcessQueue_UpdatesRevitContext()
    {
        var writer = new CapturingContextWriter();
        var awaitable = new AgentAwaitableEvent(writer);
        awaitable.Initialize(() => { });
        var app = new object();

        awaitable.ProcessQueue(app);

        Assert.Same(app, writer.RevitApplication);
    }

    private sealed class CapturingContextWriter : IRevitContextWriter
    {
        public object? RevitApplication { get; private set; }

        public void SetContext(object revitUIApplication) =>
            RevitApplication = revitUIApplication;
    }
}
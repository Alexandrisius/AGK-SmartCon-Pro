using SmartCon.Core.Threading;
using Xunit;

namespace SmartCon.Tests.Core.Threading;

public sealed class PauseGateTests
{
    [Fact]
    public void Initial_IsNotPaused()
    {
        var gate = new PauseGate();
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public void Pause_SetsIsPaused()
    {
        var gate = new PauseGate();
        gate.Pause();
        Assert.True(gate.IsPaused);
    }

    [Fact]
    public void Pause_IsIdempotent()
    {
        var gate = new PauseGate();
        gate.Pause();
        gate.Pause();
        Assert.True(gate.IsPaused);
        gate.Resume();
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public async Task WaitWhilePausedAsync_NotPaused_CompletesImmediately()
    {
        var gate = new PauseGate();
        var task = gate.WaitWhilePausedAsync();
        Assert.True(task.IsCompleted);
        await task;
    }

    [Fact]
    public async Task WaitWhilePausedAsync_Paused_CompletesAfterResume()
    {
        var gate = new PauseGate();
        gate.Pause();

        var task = gate.WaitWhilePausedAsync();
        Assert.False(task.IsCompleted);

        gate.Resume();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(task.IsCompleted);
    }

    [Fact]
    public async Task Resume_WithoutPause_IsNoOp()
    {
        var gate = new PauseGate();
        gate.Resume();
        Assert.False(gate.IsPaused);
        await gate.WaitWhilePausedAsync();
    }

    [Fact]
    public async Task PauseResume_MultipleCycles_Work()
    {
        var gate = new PauseGate();
        for (var i = 0; i < 3; i++)
        {
            gate.Pause();
            var task = gate.WaitWhilePausedAsync();
            Assert.False(task.IsCompleted);
            gate.Resume();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(gate.IsPaused);
    }
}

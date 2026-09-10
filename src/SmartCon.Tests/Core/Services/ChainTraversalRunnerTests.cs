using SmartCon.Core.Services;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class ChainTraversalRunnerTests
{
    [Fact]
    public void Run_ReachesTarget_ReturnsCompleted()
    {
        var depth = 0;
        var result = ChainTraversalRunner.Run(0, 3, 30, () => ++depth);

        Assert.Equal(ChainTraversalStopReason.Completed, result.StopReason);
        Assert.Equal(3, result.FinalDepth);
        Assert.Equal(3, result.Processed);
    }

    [Fact]
    public void Run_StepFails_StopsImmediately_KeepsDepth()
    {
        var depth = 0;
        var calls = 0;
        var result = ChainTraversalRunner.Run(0, 10, 30, () =>
        {
            calls++;
            if (calls == 2) return null;
            return ++depth;
        });

        Assert.Equal(ChainTraversalStopReason.StepFailed, result.StopReason);
        Assert.Equal(1, result.FinalDepth);
        Assert.Equal(1, result.Processed);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Run_StepSucceedsButDepthStalls_StopsWithNoProgress()
    {
        var calls = 0;
        var result = ChainTraversalRunner.Run(0, 10, 30, () =>
        {
            calls++;
            return 0;
        });

        Assert.Equal(ChainTraversalStopReason.NoProgress, result.StopReason);
        Assert.Equal(0, result.FinalDepth);
        Assert.Equal(0, result.Processed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Run_StepReturnsLowerDepth_StopsWithNoProgress()
    {
        var depth = 0;
        var calls = 0;
        var result = ChainTraversalRunner.Run(0, 10, 30, () =>
        {
            calls++;
            if (calls == 1) return ++depth;
            return depth - 1;
        });

        Assert.Equal(ChainTraversalStopReason.NoProgress, result.StopReason);
        Assert.Equal(1, result.FinalDepth);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Run_RespectsMaxLevel()
    {
        var depth = 0;
        var result = ChainTraversalRunner.Run(0, 100, 5, () => ++depth);

        Assert.Equal(ChainTraversalStopReason.Completed, result.StopReason);
        Assert.Equal(5, result.FinalDepth);
        Assert.Equal(5, result.Processed);
    }

    [Fact]
    public void Run_StartEqualsTarget_NoSteps()
    {
        var calls = 0;
        var result = ChainTraversalRunner.Run(4, 4, 30, () =>
        {
            calls++;
            return 5;
        });

        Assert.Equal(ChainTraversalStopReason.Completed, result.StopReason);
        Assert.Equal(4, result.FinalDepth);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Run_NullStep_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ChainTraversalRunner.Run(0, 1, 30, null!));
    }
}

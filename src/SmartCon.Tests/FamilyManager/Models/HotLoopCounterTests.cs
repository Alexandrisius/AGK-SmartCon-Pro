using SmartCon.Core.Logging;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

public sealed class HotLoopCounterTests
{
    [Fact]
    public void ShouldLog_AtSampleEvery_ReturnsTrue()
    {
        var counter = new HotLoopCounter(1024);
        for (int i = 0; i < 1023; i++) counter.ShouldLog();
        Assert.True(counter.ShouldLog());
    }

    [Fact]
    public void ShouldLog_FirstCall_ReturnsFalse()
    {
        var counter = new HotLoopCounter(1024);
        Assert.False(counter.ShouldLog());
    }

    [Fact]
    public void ShouldLog_EmitsEverySampleEvery()
    {
        var counter = new HotLoopCounter(1024);
        var emissions = 0;
        for (int i = 0; i < 10240; i++)
        {
            if (counter.ShouldLog()) emissions++;
        }
        Assert.Equal(10, emissions);
    }

    [Fact]
    public void ShouldLog_SampleEvery1_EmitsEveryCall()
    {
        var counter = new HotLoopCounter(1);
        var emissions = 0;
        for (int i = 0; i < 100; i++)
        {
            if (counter.ShouldLog()) emissions++;
        }
        Assert.Equal(100, emissions);
    }

    [Fact]
    public void ShouldLog_NonPowerOfTwo_RoundsUpToPowerOfTwo()
    {
        var counter = new HotLoopCounter(1000);
        Assert.Equal(1024, counter.SampleEvery);
    }

    [Fact]
    public void ShouldLog_ZeroOrNegative_CoercedToOne()
    {
        var c1 = new HotLoopCounter(0);
        var c2 = new HotLoopCounter(-5);
        Assert.Equal(1, c1.SampleEvery);
        Assert.Equal(1, c2.SampleEvery);
    }

    [Fact]
    public void Count_TracksTotalCalls()
    {
        var counter = new HotLoopCounter(1024);
        for (int i = 0; i < 50; i++) counter.ShouldLog();
        Assert.Equal(50, counter.Count);
    }

    [Fact]
    public void Count_AccurateAfterManyCalls()
    {
        var counter = new HotLoopCounter(16);
        for (int i = 0; i < 1000000; i++) counter.ShouldLog();
        Assert.Equal(1000000, counter.Count);
    }
}

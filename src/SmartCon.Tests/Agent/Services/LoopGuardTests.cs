using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class LoopGuardTests
{
    [Fact]
    public void FirstTwoIdenticalCalls_AreAllowed_ThirdIsBlocked()
    {
        var guard = new LoopGuard();

        Assert.Equal(LoopGuardAction.Allow, guard.BeforeCall("list_views", "{}").Action);
        Assert.Equal(LoopGuardAction.Allow, guard.BeforeCall("list_views", "{}").Action);

        var third = guard.BeforeCall("list_views", "{}");

        Assert.Equal(LoopGuardAction.Block, third.Action);
        Assert.Equal(3, third.IdenticalCalls);
        Assert.NotNull(third.Hint);
    }

    [Fact]
    public void FourthCall_IsBlocked_FifthAborts()
    {
        var guard = new LoopGuard();
        for (var index = 0; index < 3; index++)
            guard.BeforeCall("list_views", "{}");

        Assert.Equal(LoopGuardAction.Block, guard.BeforeCall("list_views", "{}").Action);

        var fifth = guard.BeforeCall("list_views", "{}");

        Assert.Equal(LoopGuardAction.Abort, fifth.Action);
        Assert.Equal(5, fifth.IdenticalCalls);
        Assert.NotNull(fifth.Hint);
    }

    [Fact]
    public void DifferentArguments_HaveIndependentCounters()
    {
        var guard = new LoopGuard();
        guard.BeforeCall("list_views", "{\"viewId\":1}");
        guard.BeforeCall("list_views", "{\"viewId\":1}");

        var otherArguments = guard.BeforeCall("list_views", "{\"viewId\":2}");
        var otherTool = guard.BeforeCall("get_view_settings", "{}");

        Assert.Equal(LoopGuardAction.Allow, otherArguments.Action);
        Assert.Equal(1, otherArguments.IdenticalCalls);
        Assert.Equal(LoopGuardAction.Allow, otherTool.Action);
    }

    [Fact]
    public void WhitespaceAroundArguments_DoesNotCreateADifferentCall()
    {
        var guard = new LoopGuard();
        guard.BeforeCall("list_views", "  {}  ");
        guard.BeforeCall("list_views", "{}");

        Assert.Equal(LoopGuardAction.Block, guard.BeforeCall("list_views", " {} ").Action);
    }

    [Fact]
    public void TwoFailedCalls_ProduceOneWarning()
    {
        var guard = new LoopGuard();
        guard.BeforeCall("rename_views", "{}");
        guard.BeforeCall("rename_views", "{}");

        var first = guard.ObserveResult("rename_views", "{}", success: false);
        var second = guard.ObserveResult("rename_views", "{}", success: false);
        var third = guard.ObserveResult("rename_views", "{}", success: false);

        Assert.False(first.WarnOnly);
        Assert.True(second.WarnOnly);
        Assert.Equal(2, second.IdenticalErrors);
        Assert.NotNull(second.Hint);
        Assert.Contains("failed twice", second.Hint);
        Assert.False(third.WarnOnly);
    }

    [Fact]
    public void SuccessResetsTheErrorCounter()
    {
        var guard = new LoopGuard();
        guard.ObserveResult("rename_views", "{}", success: false);
        guard.ObserveResult("rename_views", "{}", success: true);

        Assert.False(guard.ObserveResult("rename_views", "{}", success: false).WarnOnly);
        Assert.True(guard.ObserveResult("rename_views", "{}", success: false).WarnOnly);
    }

    [Fact]
    public void Reset_ClearsAllCounters()
    {
        var guard = new LoopGuard();
        guard.BeforeCall("list_views", "{}");
        guard.BeforeCall("list_views", "{}");
        guard.BeforeCall("list_views", "{}");
        guard.ObserveResult("list_views", "{}", success: false);

        guard.Reset();

        var afterReset = guard.BeforeCall("list_views", "{}");
        Assert.Equal(LoopGuardAction.Allow, afterReset.Action);
        Assert.Equal(1, afterReset.IdenticalCalls);

        var firstError = guard.ObserveResult("list_views", "{}", success: false);
        Assert.False(firstError.WarnOnly);
        Assert.Equal(1, firstError.IdenticalErrors);
    }

    [Fact]
    public void EntryCap_EvictsTheOldestHashes()
    {
        var guard = new LoopGuard();
        var firstArguments = "{\"index\":0}";
        guard.BeforeCall("query_elements", firstArguments);
        guard.BeforeCall("query_elements", firstArguments);

        for (var index = 1; index <= LoopGuard.MaxEntries; index++)
        {
            guard.BeforeCall("query_elements", "{\"index\":" + index + "}");
            guard.BeforeCall("query_elements", "{\"index\":" + index + "}");
        }

        var evicted = guard.BeforeCall("query_elements", firstArguments);

        Assert.Equal(LoopGuardAction.Allow, evicted.Action);
        Assert.Equal(1, evicted.IdenticalCalls);
    }

    [Fact]
    public void Hash_IsStableAndNormalizesWhitespace()
    {
        var first = LoopGuard.Hash("list_views", " {\"a\":1} ");
        var second = LoopGuard.Hash("list_views", "{\"a\":1}");
        var otherTool = LoopGuard.Hash("rename_views", "{\"a\":1}");

        Assert.Equal(first, second);
        Assert.NotEqual(first, otherTool);
    }
}

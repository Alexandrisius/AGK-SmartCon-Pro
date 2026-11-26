using Xunit;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Tests.Agent.Services;

public sealed class PaginationGuardTests
{
    private const string Tool = "get_schedule_elements";

    [Fact]
    public void Canonical_SortedKeys_SameCallSpellingSharesKey()
    {
        var left = ToolCallIdentity.Canonical(
            Tool, "{\"limit\":500,\"offset\":0,\"scheduleId\":1884951}");
        var right = ToolCallIdentity.Canonical(
            Tool, "{\"scheduleId\":1884951,\"offset\":0,\"limit\":500}");

        Assert.Equal(left, right);
    }

    [Fact]
    public void Canonical_IgnoringPageArguments_MergesPagesOfOneObject()
    {
        var page1 = ToolCallIdentity.Canonical(
            Tool, "{\"scheduleId\":1884951,\"limit\":1,\"offset\":0}", ignorePageArguments: true);
        var page2 = ToolCallIdentity.Canonical(
            Tool, "{\"offset\":436,\"scheduleId\":1884951,\"limit\":3}", ignorePageArguments: true);

        Assert.Equal(page1, page2);
        Assert.NotNull(page1);
    }

    [Fact]
    public void Canonical_NoPageArguments_ReturnsNullForTargetIdentity()
    {
        Assert.Null(ToolCallIdentity.Canonical(
            Tool, "{\"scheduleId\":1884951}", ignorePageArguments: true));
        Assert.Null(ToolCallIdentity.Canonical(Tool, "not json"));
        Assert.Null(ToolCallIdentity.Canonical(Tool, "[1,2]"));
    }

    [Fact]
    public void BeforeCall_PagesOfOneObject_WarnOnFourthBlockOnSixth()
    {
        var guard = new PaginationGuard();

        var first = guard.BeforeCall(Tool, "{\"scheduleId\":1,\"limit\":1,\"offset\":0}");
        var second = guard.BeforeCall(Tool, "{\"scheduleId\":1,\"limit\":2,\"offset\":1}");
        var third = guard.BeforeCall(Tool, "{\"scheduleId\":1,\"limit\":3,\"offset\":2}");
        var fourth = guard.BeforeCall(Tool, "{\"scheduleId\":1,\"limit\":1,\"offset\":3}");
        var fifth = guard.BeforeCall(Tool, "{\"scheduleId\":1,\"limit\":2,\"offset\":4}");
        var sixth = guard.BeforeCall(Tool, "{\"scheduleId\":1,\"limit\":3,\"offset\":5}");

        Assert.Equal(PaginationGuardAction.Proceed, first.Action);
        Assert.Equal(PaginationGuardAction.Proceed, second.Action);
        Assert.Equal(PaginationGuardAction.Proceed, third.Action);
        Assert.Equal(PaginationGuardAction.Warn, fourth.Action);
        Assert.Equal(PaginationGuardAction.Warn, fifth.Action);
        Assert.Equal(PaginationGuardAction.Block, sixth.Action);
        Assert.Contains("read_tool_result", sixth.Hint);
    }

    [Fact]
    public void BeforeCall_OtherObjectOrNoPaging_DoesNotCount()
    {
        var guard = new PaginationGuard();

        for (var index = 0; index < 10; index++)
        {
            // A different target object per call: ten reads, not one read paged.
            Assert.Equal(
                PaginationGuardAction.Proceed,
                guard.BeforeCall(Tool, "{\"scheduleId\":" + (100 + index) + ",\"limit\":1}")
                    .Action);
            Assert.Equal(
                PaginationGuardAction.Proceed,
                guard.BeforeCall("summarize_elements", "{\"category\":\"Walls\"}").Action);
        }
    }

    [Fact]
    public void BeforeCall_Decay_ResetsAfterQuietPeriod()
    {
        var now = DateTimeOffset.UtcNow;
        var guard = new PaginationGuard(() => now);

        for (var index = 0; index < 6; index++)
            guard.BeforeCall(Tool, "{\"scheduleId\":1,\"limit\":1,\"offset\":" + index + "}");

        var blocked = guard.BeforeCall(Tool, "{\"scheduleId\":1,\"limit\":1,\"offset\":9}");
        Assert.Equal(PaginationGuardAction.Block, blocked.Action);

        now += TimeSpan.FromMinutes(11);
        var fresh = guard.BeforeCall(Tool, "{\"scheduleId\":1,\"limit\":1,\"offset\":10}");
        Assert.Equal(PaginationGuardAction.Proceed, fresh.Action);
    }

    [Fact]
    public void BeforeCall_CountsSurviveAcrossTurns_ByInstance()
    {
        // The orchestrator keeps one guard per conversation for all turns: the
        // counts must not depend on a turn boundary.
        var guard = new PaginationGuard();

        guard.BeforeCall(Tool, "{\"scheduleId\":1,\"limit\":1,\"offset\":0}");
        guard.BeforeCall(Tool, "{\"scheduleId\":1,\"limit\":1,\"offset\":1}");
        guard.BeforeCall(Tool, "{\"scheduleId\":1,\"limit\":1,\"offset\":2}");

        var afterUserMessage = guard.BeforeCall(
            Tool, "{\"scheduleId\":1,\"limit\":1,\"offset\":3}");

        Assert.Equal(PaginationGuardAction.Warn, afterUserMessage.Action);
    }
}

using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentUndoChainTests
{
    private static ChangeSetHeader Header(
        Guid id,
        DateTime appliedAt,
        DateTime createdAt = default) => new()
    {
        Id = id,
        Operation = "set_parameters",
        DocumentId = "doc",
        CreatedAt = createdAt != default
            ? createdAt
            : appliedAt != default
                ? appliedAt.AddSeconds(-1)
                : new DateTime(2026, 1, 1),
        AppliedAt = appliedAt != default ? appliedAt : null
    };

    [Fact]
    public void Build_FromNewestToTargetInclusiveInReverseOrder()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var headers = new[]
        {
            Header(third, new DateTime(2026, 1, 1, 0, 0, 3)),
            Header(second, new DateTime(2026, 1, 1, 0, 0, 2)),
            Header(first, new DateTime(2026, 1, 1, 0, 0, 1))
        };

        var (chain, error, _) = AgentUndoChain.Build(first, headers);

        Assert.Null(error);
        Assert.Equal(new[] { third, second, first }, chain.Select(header => header.Id));
    }

    [Fact]
    public void Build_SingleSetChain_WhenTargetIsTheNewest()
    {
        var newest = Guid.NewGuid();
        var older = Guid.NewGuid();
        var headers = new[]
        {
            Header(newest, new DateTime(2026, 1, 1, 0, 0, 2)),
            Header(older, new DateTime(2026, 1, 1, 0, 0, 1))
        };

        var (chain, error, _) = AgentUndoChain.Build(newest, headers);

        Assert.Null(error);
        Assert.Equal(new[] { newest }, chain.Select(header => header.Id));
    }

    [Fact]
    public void Build_SkipsRolledBackAndDiscardedSets()
    {
        var oldest = Guid.NewGuid();
        var rolledBack = Guid.NewGuid();
        var newest = Guid.NewGuid();
        var headers = new[]
        {
            Header(newest, new DateTime(2026, 1, 1, 0, 0, 3)),
            Header(rolledBack, default), // rolled back: AppliedAt is null
            Header(oldest, new DateTime(2026, 1, 1, 0, 0, 1))
        };

        var (chain, error, _) = AgentUndoChain.Build(oldest, headers);

        Assert.Null(error);
        Assert.Equal(new[] { newest, oldest }, chain.Select(header => header.Id));
    }

    [Fact]
    public void Build_TargetNotApplied_ChangeSetNotInChain()
    {
        var notApplied = Guid.NewGuid();
        var headers = new[]
        {
            Header(Guid.NewGuid(), new DateTime(2026, 1, 1, 0, 0, 1)),
            Header(notApplied, default)
        };

        var (chain, error, message) = AgentUndoChain.Build(notApplied, headers);

        Assert.Empty(chain);
        Assert.Equal("change_set_not_in_chain", error);
        Assert.Contains(notApplied.ToString("D"), message);
    }

    [Fact]
    public void Build_UnknownTarget_ChangeSetNotInChain()
    {
        var headers = new[] { Header(Guid.NewGuid(), new DateTime(2026, 1, 1)) };

        var (chain, error, _) = AgentUndoChain.Build(Guid.NewGuid(), headers);

        Assert.Empty(chain);
        Assert.Equal("change_set_not_in_chain", error);
    }

    [Fact]
    public void Build_SameAppliedAt_FallsBackToCreatedAt()
    {
        var at = new DateTime(2026, 1, 1, 0, 0, 0);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var headers = new[]
        {
            Header(first, at, new DateTime(2026, 1, 1, 0, 0, 5)),
            Header(second, at, new DateTime(2026, 1, 1, 0, 0, 9))
        };

        var (chain, error, _) = AgentUndoChain.Build(first, headers);

        Assert.Null(error);
        Assert.Equal(new[] { second, first }, chain.Select(header => header.Id));
    }
}

using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Question budget of classify_types (S.49, #534): the session fb3b66f6 put
/// 416 prompts on the decision channel in one call and the free model dropped
/// the connection, while narrow calls answered in seconds. The run is chunked
/// by the ESTIMATE 1 + types per family - and a family above the budget is
/// never deferred forever, or the agent would loop on it.
/// </summary>
public sealed class AgentClassificationBudgetTests
{
    private static ClassifyFamilyGroup Family(string name, int typeCount)
    {
        var types = Enumerable.Range(1, typeCount)
            .Select(index => new ClassifyTypeSnapshot(
                index, "ОВ", name, null, name + " Исполнение " + index,
                null, Array.Empty<string>()))
            .ToArray();
        var group = new ClassifyFamilyGroup { Key = name, Category = "ОВ", FamilyName = name };
        // Types has an internal setter (composed by the snapshot stage); the
        // budget math only needs the count.
        typeof(ClassifyFamilyGroup)
            .GetProperty(nameof(ClassifyFamilyGroup.Types))!
            .SetValue(group, types);
        return group;
    }

    [Fact]
    public void Estimate_IsOnePerFamilyPlusOnePerType()
    {
        var groups = new[] { Family("Кран", 3), Family("Тройник", 40) };

        Assert.Equal(4, AgentClassificationBudget.EstimateQuestions(groups[0]));
        Assert.Equal(41, AgentClassificationBudget.EstimateQuestions(groups[1]));
        Assert.Equal(45, AgentClassificationBudget.EstimateQuestions(groups));
    }

    [Fact]
    public void Chunk_FitsInOneChunk_WhenEstimateIsWithinBudget()
    {
        var groups = new[] { Family("A", 10), Family("B", 10) };

        var chunks = AgentClassificationBudget.ChunkByQuestionBudget(groups, 40, out var oversized);

        Assert.Single(chunks);
        Assert.Empty(oversized);
        Assert.Equal(2, chunks[0].Count);
    }

    [Fact]
    public void Chunk_SplitsByBudget_KeepingOrder()
    {
        var groups = new[]
        {
            Family("A", 5), Family("B", 5), Family("C", 20), Family("D", 8)
        };

        var chunks = AgentClassificationBudget.ChunkByQuestionBudget(groups, 40, out var oversized);

        Assert.Empty(oversized);
        Assert.Equal(2, chunks.Count);
        // A+B+C = 6+6+21 = 33 <= 40, D (est 9) does not fit -> second chunk.
        Assert.Equal(3, chunks[0].Count);
        Assert.Equal("D", chunks[1][0].FamilyName);
        foreach (var chunk in chunks)
            Assert.True(AgentClassificationBudget.EstimateQuestions(chunk) <= 40);
    }

    [Fact]
    public void Chunk_OversizedFamilyGoesAlone_NeverDroppedOrDeferred()
    {
        // The loop-safety rule (S.49): a family above the budget still runs -
        // alone - instead of being returned as "not processed" forever.
        var groups = new[]
        {
            Family("Мегасемейство", 100), Family("B", 3), Family("C", 3)
        };

        var chunks = AgentClassificationBudget.ChunkByQuestionBudget(groups, 40, out var oversized);

        Assert.Single(oversized);
        Assert.Equal("Мегасемейство", oversized[0].FamilyName);
        Assert.Equal(2, chunks.Count);
        Assert.Single(chunks[0]);
        Assert.Equal("Мегасемейство", chunks[0][0].FamilyName);
        // The normal families are chunked together after it.
        Assert.Equal(2, chunks[1].Count);
    }

    [Theory]
    [InlineData(null, 40)]
    [InlineData(0, 10)]
    [InlineData(5, 10)]
    [InlineData(40, 40)]
    [InlineData(999999, 2000)]
    public void Normalize_ClampsIntoTheConfiguredRange(int? value, int expected)
    {
        Assert.Equal(expected, AgentClassificationBudget.Normalize(value));
    }
}

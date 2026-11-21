using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class ToolCardRouterTests
{
    private static IReadOnlyList<AgentToolDefinition> EnrichedCatalog() =>
        AgentToolCards.All
            .Select(card => AgentToolCatalog.Enrich(new AgentToolDefinition
            {
                Name = card.ToolName,
                Description = card.Purpose,
                InputSchemaJson = @"{""type"":""object"",""properties"":{}}"
            }))
            .ToList();

    private static AgentToolDefinition Stub(
        string name,
        string purpose,
        params string[] negativeExamples) =>
        new()
        {
            Name = name,
            Description = purpose,
            InputSchemaJson = @"{""type"":""object"",""properties"":{}}",
            Purpose = purpose,
            NegativeExamples = negativeExamples
        };

    [Theory]
    [InlineData("переименуй виды", "rename_views")]
    [InlineData("применить шаблон", "apply_view_template")]
    [InlineData("скрой категорию", "set_view_visibility")]
    [InlineData("сколько элементов", "summarize_elements")]
    [InlineData("массовая миграция параметра по семействам", "start_job")]
    public void Rank_EnrichedCatalog_PutsExpectedToolFirst(string query, string expected)
    {
        var ranked = ToolCardRouter.Rank(query, EnrichedCatalog(), 5);

        Assert.NotEmpty(ranked);
        Assert.Equal(expected, ranked[0].Name);
    }

    [Theory]
    [InlineData("переименуй виды", "rename_views")]
    [InlineData("применить шаблон", "apply_view_template")]
    [InlineData("скрой категорию", "set_view_visibility")]
    [InlineData("сколько элементов", "summarize_elements")]
    [InlineData("массовое переименование видов", "start_job")]
    public void Rank_EnrichedCatalog_KeepsExpectedToolInTopThree(string query, string expected)
    {
        var ranked = ToolCardRouter.Rank(query, EnrichedCatalog(), 3);

        Assert.Contains(ranked, tool => string.Equals(tool.Name, expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Rank_NegativeExampleQuery_DoesNotPickThePenalizedToolFirst()
    {
        var ranked = ToolCardRouter.Rank(
            "Переименуй вид SC_1 в SC_2", EnrichedCatalog(), 5);

        Assert.NotEmpty(ranked);
        Assert.NotEqual("list_views", ranked[0].Name);
        Assert.Equal("rename_views", ranked[0].Name);
    }

    [Fact]
    public void Rank_NegativeExamples_LowerTheScore()
    {
        var positive = Stub("alpha_tool", "remove background from images");
        var penalized = Stub(
            "beta_tool",
            "remove background remove background remove background",
            "remove background");

        var ranked = ToolCardRouter.Rank(
            "remove background", new[] { penalized, positive }, 2);

        Assert.Equal(new[] { "alpha_tool", "beta_tool" }, ranked.Select(tool => tool.Name).ToArray());
    }

    [Fact]
    public void Rank_NoSignal_ReturnsEmpty()
    {
        var ranked = ToolCardRouter.Rank("xyzzy", EnrichedCatalog(), 5);

        Assert.Empty(ranked);
    }

    [Fact]
    public void Rank_RespectsMaxAndTieBreaksByName()
    {
        var first = Stub("beta_tool", "rename views");
        var second = Stub("alpha_tool", "rename views");

        var ranked = ToolCardRouter.Rank("rename views", new[] { first, second }, 1);

        Assert.Single(ranked);
        Assert.Equal("alpha_tool", ranked[0].Name);

        var both = ToolCardRouter.Rank("rename views", new[] { first, second }, 5);
        Assert.Equal(new[] { "alpha_tool", "beta_tool" }, both.Select(tool => tool.Name).ToArray());
    }

    [Fact]
    public void TopCandidates_DefaultCapsAtFifty()
    {
        var tools = Enumerable.Range(0, 60)
            .Select(index => Stub("rename_tool_" + index, "rename views"))
            .ToList();

        var top = ToolCardRouter.TopCandidates("rename views", tools);

        Assert.Equal(50, top.Count);
    }

    [Fact]
    public void Rank_EmptyQuery_ReturnsEmpty()
    {
        Assert.Empty(ToolCardRouter.Rank(string.Empty, EnrichedCatalog(), 5));
        Assert.Empty(ToolCardRouter.Rank("   ", EnrichedCatalog(), 5));
    }
}

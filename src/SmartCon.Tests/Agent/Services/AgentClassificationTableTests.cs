using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentClassificationTableTests
{
    private static AgentClassificationEntry Row(
        string code,
        string description,
        int level,
        string? parent = null) =>
        new()
        {
            Code = code,
            Description = description,
            Level = level,
            ParentCode = parent
        };

    private static readonly AgentClassificationEntry[] Rows =
    {
        Row("30.30", "Арматура обратная", 2, "30"),
        Row("30.20.10", "Кран шаровой", 3, "30.20"),
        Row("40", "Оборудование", 1),
        Row("30.20", "Арматура запорная", 2, "30"),
        Row("30", "Трубопроводная арматура", 1),
        Row("30.20.10.10", "Кран шаровой латунный", 4, "30.20.10"),
        Row("30.30.10", "Клапан обратный", 3, "30.30"),
        Row("30.20.20", "Задвижка", 3, "30.20"),
        Row("40.10", "Насосы", 2, "40"),
        Row("40.10.10", "Насос циркуляционный", 3, "40.10")
    };

    [Fact]
    public void Build_UnsortedRows_PutsParentBeforeChildrenInCodeOrder()
    {
        var table = AgentClassificationTable.Build(Rows, @"C:\classifier.txt");

        Assert.Equal(
            new[]
            {
                "30", "30.20", "30.20.10", "30.20.10.10", "30.20.20", "30.30", "30.30.10",
                "40", "40.10", "40.10.10"
            },
            table.Entries.Select(entry => entry.Code).ToArray());
        Assert.True(table.Loaded);
        Assert.Equal(@"C:\classifier.txt", table.Source);
    }

    [Fact]
    public void Build_Breadcrumb_JoinsDescriptionsFromRoot()
    {
        var table = AgentClassificationTable.Build(Rows, null);

        Assert.Equal(
            "Трубопроводная арматура › Арматура запорная › Кран шаровой › Кран шаровой латунный",
            table.Find("30.20.10.10")!.Breadcrumb);
        Assert.Equal("Оборудование", table.Find("40")!.Breadcrumb);
    }

    [Fact]
    public void Build_LeafFlag_TrueOnlyForRowsWithoutChildren()
    {
        var table = AgentClassificationTable.Build(Rows, null);

        Assert.True(table.Find("30.20.10.10")!.IsLeaf);
        Assert.False(table.Find("30.20.10")!.IsLeaf);
        Assert.False(table.Find("40")!.IsLeaf);
    }

    [Fact]
    public void Find_IgnoresCaseAndWhitespace()
    {
        var table = AgentClassificationTable.Build(Rows, null);

        Assert.Equal("30.20.10", table.Find("  30.20.10 ")!.Code);
        Assert.NotNull(AgentClassificationTable.Build(
            new[] { Row("a1010", "Foundations", 1) }, null).Find("A1010"));
        Assert.Null(table.Find("30.99"));
        Assert.Null(table.Find(null));
        Assert.Null(table.Find(""));
    }

    [Fact]
    public void Build_BlankAndDuplicateCodes_BlankDroppedFirstDuplicateKept()
    {
        var table = AgentClassificationTable.Build(
            new[]
            {
                Row("10", "First", 1),
                Row("", "Blank", 1),
                Row("  ", "Spaces", 1),
                Row("10", "Second", 1)
            },
            null);

        var entry = Assert.Single(table.Entries);
        Assert.Equal("First", entry.Description);
    }

    [Fact]
    public void Build_UnknownAndSelfParent_BecomesRoot()
    {
        var table = AgentClassificationTable.Build(
            new[]
            {
                Row("10", "Root", 1, "10"),
                Row("20", "Orphan", 1, "missing"),
                Row("20.10", "Child of orphan", 2, "20")
            },
            null);

        Assert.Null(table.Find("10")!.ParentCode);
        Assert.Null(table.Find("20")!.ParentCode);
        Assert.Equal("Orphan › Child of orphan", table.Find("20.10")!.Breadcrumb);
    }

    [Fact]
    public void Build_ParentCycle_TerminatesAndKeepsEveryRow()
    {
        var table = AgentClassificationTable.Build(
            new[]
            {
                Row("10", "Left", 1, "20"),
                Row("20", "Right", 1, "10")
            },
            null);

        Assert.Equal(2, table.Entries.Count);
        Assert.All(table.Entries, entry => Assert.NotEmpty(entry.Breadcrumb));
    }

    [Fact]
    public void Build_NoDescription_FallsBackToCodeInBreadcrumb()
    {
        var table = AgentClassificationTable.Build(
            new[] { Row("10", "", 1), Row("10.10", "Child", 2, "10") },
            null);

        Assert.Equal("10 › Child", table.Find("10.10")!.Breadcrumb);
    }

    [Fact]
    public void Empty_NoRows_IsNotLoaded()
    {
        Assert.False(AgentClassificationTable.Empty.Loaded);
        Assert.Empty(AgentClassificationTable.Empty.Entries);
        Assert.Null(AgentClassificationTable.Empty.Source);
        Assert.Null(AgentClassificationTable.Empty.Find("30"));
        Assert.Empty(AgentClassificationTable.Empty.Suggest("30"));
    }

    [Fact]
    public void Build_BlankSource_ReportsNoSource()
    {
        Assert.Null(AgentClassificationTable.Build(Rows, "  ").Source);
    }

    [Fact]
    public void Search_MatchesCodeDescriptionAndBreadcrumbCaseInsensitively()
    {
        var table = AgentClassificationTable.Build(Rows, null);

        Assert.Equal(
                new[] { "30.20.10", "30.20.10.10" },
                table.Search(contains: "кран ШАР").Select(entry => entry.Code).ToArray());
        Assert.Equal(
                new[] { "30", "30.20", "30.20.10", "30.20.10.10", "30.20.20", "30.30", "30.30.10" },
                table.Search(contains: "трубопроводн").Select(entry => entry.Code).ToArray());
        Assert.Empty(table.Search(contains: "насос", parentCode: "30"));
        Assert.Empty(table.Search(contains: "nothing-like-this"));
    }

    [Fact]
    public void Search_SubtreeLevelLeafAndPaging_NarrowInTreeOrder()
    {
        var table = AgentClassificationTable.Build(Rows, null);

        Assert.Equal(
            new[] { "30", "30.20", "30.20.10", "30.20.10.10", "30.20.20", "30.30", "30.30.10" },
            table.Search(parentCode: "30").Select(entry => entry.Code).ToArray());
        Assert.Equal(
            new[] { "30", "40" },
            table.Search(level: 1).Select(entry => entry.Code).ToArray());
        Assert.Equal(
            new[] { "30.20.10.10", "30.20.20", "30.30.10", "40.10.10" },
            table.Search(leafOnly: true).Select(entry => entry.Code).ToArray());
        Assert.Equal(
            new[] { "30.20.10.10", "30.20.20", "30.30" },
            table.Search(limit: 3, offset: 3).Select(entry => entry.Code).ToArray());
        Assert.Equal(table.Entries, table.Search(limit: int.MaxValue));
    }

    [Fact]
    public void Suggest_MissingCode_SiblingsFirstThenNearestCodes()
    {
        var table = AgentClassificationTable.Build(Rows, null);

        var suggestions = table.Suggest("30.20.30", 8);

        Assert.Equal(
            new[] { "30.20.10", "30.20.20", "30.30.10" },
            suggestions.Select(entry => entry.Code).ToArray());
    }

    [Fact]
    public void Suggest_RootLevelCode_OffersRoots()
    {
        var table = AgentClassificationTable.Build(Rows, null);

        Assert.Equal(
            new[] { "30", "40" },
            table.Suggest("50", 8).Select(entry => entry.Code).ToArray());
    }

    [Fact]
    public void Suggest_RespectsMaxAndRejectsNonPositive()
    {
        var table = AgentClassificationTable.Build(Rows, null);

        Assert.Single(table.Suggest("30.20.30", 1));
        Assert.Empty(table.Suggest("30.20.30", 0));
    }

    [Theory]
    [InlineData("30", "30.20", -1)]
    [InlineData("30.20", "30.20.10", -1)]
    [InlineData("30.20.10", "30.30", -1)]
    [InlineData("9", "10", -1)]
    [InlineData("10.2", "10.10", -1)]
    [InlineData("30.20.10", "30.20.10", 0)]
    [InlineData("a.b", "a.b", 0)]
    public void CodeComparer_SegmentsCompareNaturally(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(AgentClassificationTable.CodeComparer.Compare(left, right)));
    }
}

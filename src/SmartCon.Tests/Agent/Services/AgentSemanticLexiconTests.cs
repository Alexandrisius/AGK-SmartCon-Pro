using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// The deterministic half of the classification match: tokenization and the
/// lexical prefilter that decides whether a family is strong or has to be
/// walked down the table.
/// </summary>
public sealed class AgentSemanticLexiconTests
{
    private static AgentClassificationEntry Row(
        string code, string description, int level, string? parent) =>
        new() { Code = code, Description = description, Level = level, ParentCode = parent };

    private static Dictionary<string, HashSet<string>> LeafTokens(AgentClassificationTable table) =>
        table.Entries
            .Where(entry => entry.IsLeaf)
            .ToDictionary(
                leaf => leaf.Code,
                leaf => new HashSet<string>(AgentSemanticLexicon.Tokenize(leaf.Breadcrumb)),
                StringComparer.Ordinal);

    private static AgentClassificationTable ServicesTable() =>
        AgentClassificationTable.Build(new[]
        {
            Row("10", "Shell", 1, null),
            Row("10.10", "Walls", 2, "10"),
            Row("10.10.10", "Load-bearing walls", 3, "10.10"),
            Row("10.10.20", "Partitions", 3, "10.10"),
            Row("20", "Services", 1, null),
            Row("20.10", "Pipelines", 2, "20"),
            Row("20.10.10", "Pipes", 3, "20.10"),
            Row("20.20", "Valves", 2, "20"),
            Row("20.20.10", "Ball valves", 3, "20.20"),
            Row("20.20.20", "Gate valves", 3, "20.20"),
            Row("30", "Other", 1, null)
        }, null);

    [Fact]
    public void Tokenize_LowerCasesFoldsYoAndSplits()
    {
        var tokens = AgentSemanticLexicon.Tokenize("Ёлки-Палки, Стены 10.20 (несущие)");

        // Pure numbers are dropped: they carry no meaning to match by.
        Assert.Equal(new[] { "елки", "палки", "стены", "несущие" }, tokens);
    }

    [Fact]
    public void Tokenize_KeepsWordsWithDigitsAndDropsPureNumbers()
    {
        Assert.Equal(
            new[] { "кран", "dn15", "пвх" },
            AgentSemanticLexicon.Tokenize("Кран DN15 200 ПВХ"));
    }

    [Fact]
    public void LexicalTop_ASizeNumberAloneIsNotALexicalHit()
    {
        var table = AgentClassificationTable.Build(new[]
        {
            Row("10", "Valves", 1, null),
            Row("10.10", "Gate valve 15", 2, "10"),
            Row("10.20", "Ball valve 200", 2, "10")
        }, null);
        var leaves = table.Entries.Where(entry => entry.IsLeaf).ToArray();

        // The only shared tokens are the numbers: this family is NOT strong.
        var scored = AgentSemanticLexicon.LexicalTop(
            new[] { "шаровой", "кран", "15" },
            new[] { "200" },
            leaves,
            LeafTokens(table),
            10);

        Assert.Empty(scored);
    }

    [Fact]
    public void TokensMatch_RequiresEqualityOrLongPrefix()
    {
        Assert.True(AgentSemanticLexicon.TokensMatch("valve", "valves"));
        Assert.True(AgentSemanticLexicon.TokensMatch("стены", "стены"));
        Assert.False(AgentSemanticLexicon.TokensMatch("wall", "walled"));
        Assert.False(AgentSemanticLexicon.TokensMatch("abc", "abcd"));
        Assert.False(AgentSemanticLexicon.TokensMatch("pipe", "valve"));
    }

    [Fact]
    public void LexicalTop_WeightsFamilyNameTwiceAndKeepsCodeOrder()
    {
        var table = ServicesTable();
        var leaves = table.Entries.Where(entry => entry.IsLeaf).ToArray();
        var leafTokens = leaves.ToDictionary(
            leaf => leaf.Code,
            leaf => new HashSet<string>(AgentSemanticLexicon.Tokenize(leaf.Breadcrumb)),
            StringComparer.Ordinal);

        var scored = AgentSemanticLexicon.LexicalTop(
            new[] { "ball", "valve", "family" },
            new[] { "gate" },
            leaves,
            leafTokens,
            2);

        // ball + valve (name, weight 2 each) + gate (weight 1) -> Ball valves
        // first (4), then Gate valves (3: valve 2 + gate 1).
        Assert.Equal(new[] { "20.20.10", "20.20.20" },
            scored.Select(leaf => leaf.Code).ToArray());
    }

}

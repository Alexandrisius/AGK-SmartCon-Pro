using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Models;

public sealed class AgentFamilyTextTests
{
    [Fact]
    public void ExtractLookupTables_FindsSingleTable()
    {
        var tables = AgentFamilyText.ExtractLookupTables(
            "size_lookup(\"PipeSizes\", \"DN\", 0 mm, Dn)");

        Assert.Single(tables);
        Assert.Equal("PipeSizes", tables[0]);
    }

    [Fact]
    public void ExtractLookupTables_FindsDistinctTablesCaseInsensitive()
    {
        var tables = AgentFamilyText.ExtractLookupTables(
            "SIZE_LOOKUP ( \"T1\" , \"A\" , 0 , Dn ) + size_lookup(\"T2\", \"B\", 0, Dn) + " +
            "size_lookup(\"T1\", \"C\", 0, Dn)");

        Assert.Equal(new[] { "T1", "T2" }, tables);
    }

    [Fact]
    public void ExtractLookupTables_ReturnsEmptyWithoutFormula()
    {
        Assert.Empty(AgentFamilyText.ExtractLookupTables(null));
        Assert.Empty(AgentFamilyText.ExtractLookupTables(string.Empty));
        Assert.Empty(AgentFamilyText.ExtractLookupTables("Width * 2"));
    }

    [Theory]
    [InlineData("  Ёлка ", "елка")]
    [InlineData("ШИРИНА", "ширина")]
    [InlineData(null, "")]
    public void NormalizeForMatch_TrimsLowercasesAndReplacesYo(string? raw, string expected)
    {
        Assert.Equal(expected, AgentFamilyText.NormalizeForMatch(raw));
    }

    [Theory]
    [InlineData("Запорный вентиль ВОЛК", "вентиль", true)]
    [InlineData("Запорный вентиль ВОЛК", "ВОЛК", true)]
    [InlineData("Ёмкость", "емкость", true)]
    [InlineData("Ширина", "высота", false)]
    [InlineData("", "ширина", false)]
    public void ContainsNormalized_MatchesLocalizedNames(string haystack, string needle, bool expected)
    {
        Assert.Equal(expected, AgentFamilyText.ContainsNormalized(haystack, needle));
    }

    [Fact]
    public void SanitizeFileName_ReplacesInvalidCharacters()
    {
        var sanitized = AgentFamilyText.SanitizeFileName("Кран/шаровый: 15\\20");

        Assert.DoesNotContain('/', sanitized);
        Assert.DoesNotContain('\\', sanitized);
        Assert.DoesNotContain(':', sanitized);
        Assert.Contains("Кран", sanitized);
    }

    [Fact]
    public void SanitizeFileName_FallsBackForEmptyName()
    {
        Assert.Equal("family", AgentFamilyText.SanitizeFileName("   "));
    }
}

public sealed class AgentFamilyPackTests
{
    private static readonly string[] ExpectedFamilyTools =
    {
        "list_families", "list_family_types", "get_family_parameters",
        "find_families_with_parameter", "list_shared_parameters",
        "rename_family_parameter", "add_family_parameter",
        "set_family_parameter_values", "set_family_formula", "map_family_parameters",
        "delete_family_types", "remove_family_parameter"
    };

    [Fact]
    public void FamiliesPack_ContainsExactlyTheExpectedTools()
    {
        var tools = AgentToolPacks.ToolsInPack("families");

        Assert.Equal(
            ExpectedFamilyTools.OrderBy(name => name, StringComparer.Ordinal),
            tools.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryFamilyTool_HasCardInFamiliesPack()
    {
        foreach (var tool in ExpectedFamilyTools)
        {
            Assert.True(AgentToolCards.TryGet(tool, out var card), tool);
            Assert.NotNull(card);
            Assert.Equal("families", card!.Pack);
            Assert.False(string.IsNullOrWhiteSpace(card.Purpose), tool + ": Purpose");
        }
    }

    [Fact]
    public void EveryFamilyTool_HasRoutingMetadataAndCategory()
    {
        foreach (var tool in ExpectedFamilyTools)
        {
            var enriched = AgentToolCatalog.Enrich(new AgentToolDefinition
            {
                Name = tool,
                Description = "test",
                InputSchemaJson = "{\"type\":\"object\"}"
            });

            Assert.False(string.IsNullOrEmpty(enriched.Category), tool + ": Category");
            Assert.Equal("families", enriched.Pack);
            Assert.True(enriched.Tags.Count > 0, tool + ": Tags");
        }
    }

    [Fact]
    public void FamiliesPack_IsNoLongerReserved()
    {
        Assert.DoesNotContain(
            AgentToolPacks.All.Where(pack => pack.Tools.Count == 0),
            pack => pack.Name == "families");
    }
}
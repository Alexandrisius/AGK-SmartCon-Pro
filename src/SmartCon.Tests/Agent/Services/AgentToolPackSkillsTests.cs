using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Gate of the pack skills (#400): every populated pack must ship a manual, a
/// reserved pack must not, and the text must be usable — a titled section with
/// recipes, a bounded size and the names of the tools it documents.
/// </summary>
public sealed class AgentToolPackSkillsTests
{
    [Fact]
    public void EveryPopulatedPack_HasSkill()
    {
        foreach (var pack in AgentToolPacks.Populated)
        {
            Assert.True(
                AgentToolPackSkills.TryGet(pack.Name, out var skill)
                && !string.IsNullOrWhiteSpace(skill),
                "Pack '" + pack.Name + "' has no skill: load_tool_pack would return no manual " +
                "for it and the model would work without the Revit-specific rules.");
        }
    }

    [Fact]
    public void ReservedPacks_HaveNoSkill()
    {
        foreach (var pack in AgentToolPacks.All.Where(pack => pack.Tools.Count == 0))
            Assert.Null(AgentToolPackSkills.TryGet(pack.Name));
    }

    [Fact]
    public void PacksWithSkill_CoverEveryPopulatedPackInCatalogOrder()
    {
        var expected = AgentToolPacks.Populated.Select(pack => pack.Name).ToArray();

        Assert.Equal(expected, AgentToolPackSkills.PacksWithSkill);
    }

    [Fact]
    public void EverySkill_HasTheSharedStructure()
    {
        foreach (var pack in AgentToolPacks.Populated)
        {
            var skill = AgentToolPackSkills.TryGet(pack.Name);
            Assert.NotNull(skill);
            Assert.StartsWith("## Пак " + pack.Name, skill, StringComparison.Ordinal);
            Assert.Contains("### Карта тулов", skill, StringComparison.Ordinal);
            Assert.Contains("### Кухня", skill, StringComparison.Ordinal);
            Assert.Contains("### Рецепты", skill, StringComparison.Ordinal);
            Assert.Contains("### Что нельзя", skill, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EverySkill_StaysWithinTheContextBudget()
    {
        foreach (var pack in AgentToolPacks.Populated)
        {
            var skill = AgentToolPackSkills.TryGet(pack.Name)!;

            // Families carries the most risk (19 tools incl. system types #441, family
            // documents) and is intentionally the longest manual; everything else stays
            // well below this ceiling.
            Assert.InRange(skill.Length, 900, 9500);
        }
    }

    [Fact]
    public void EverySkill_MentionsTheToolsItDocuments()
    {
        foreach (var pack in AgentToolPacks.Populated)
        {
            var skill = AgentToolPackSkills.TryGet(pack.Name)!;
            var mentioned = pack.Tools.Count(tool =>
                skill.Contains("`" + tool + "`", StringComparison.Ordinal));
            var required = Math.Min(3, pack.Tools.Count);

            Assert.True(
                mentioned >= required,
                "Skill of pack '" + pack.Name + "' mentions " + mentioned + " of its " +
                pack.Tools.Count + " tools; at least " + required +
                " must be documented by exact name.");
        }
    }

    [Fact]
    public void AllSkills_FitTheConversationBudget()
    {
        // load_tool_pack returns every skill of the requested packs in one result.
        // The budget protects skills from trimming (#400), so their total must stay
        // below the expanded meta-tool budget even when every pack is loaded at once.
        // #441/#348 grew the catalog (83 → 87 tools): reserve 20000 → 17500; G.5
        // (87 → 90 tools, batch recipes of sheets): 17500 → 16000.
        var total = AgentToolPacks.Populated
            .Sum(pack => AgentToolPackSkills.TryGet(pack.Name)!.Length);

        Assert.InRange(
            total,
            1000,
            SmartCon.Core.Models.Agent.ToolResultBudget.MetaMaxCharacters - 16000);
    }
}
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
            Assert.True(skill.Length is >= 900 and <= 9500,
                pack.Name + " skill is " + skill.Length + " chars (limit 9500)");
        }
    }

    [Fact]
    public void EverySkill_CarriesLineFeedsOnly()
    {
        // The bodies are verbatim strings: without the normalization a CRLF
        // checkout hands out a longer text than an LF one - and the budget
        // gate above would depend on the checkout, not on the manual.
        foreach (var pack in AgentToolPacks.Populated)
        {
            var skill = AgentToolPackSkills.TryGet(pack.Name)!;
            Assert.DoesNotContain("\r", skill);
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
        // load_tool_pack is capped at MaxPacksPerCall packs per call (#453), so the
        // worst result carries at most the five largest manuals plus the tool
        // schemas — that is the sum this gate must fit into the meta budget. The
        // budget protects skills from trimming (#400); the TOTAL of all skills no
        // longer presses, so the catalog can keep growing without re-basing this
        // gate on every new pack. Reserve history: #441/#348 grew the catalog
        // (83 → 87 tools): 20000 → 17500; G.5 (87 → 90 tools): 17500 → 16000;
        // #453 re-based the checked sum from "all packs" to "five largest".
        var worstCall = AgentToolPacks.Populated
            .Select(pack => AgentToolPackSkills.TryGet(pack.Name)!.Length)
            .OrderByDescending(length => length)
            .Take(SmartCon.Agent.Services.Tools.LoadToolPackTool.MaxPacksPerCall)
            .Sum();

        Assert.InRange(
            worstCall,
            1000,
            SmartCon.Core.Models.Agent.ToolResultBudget.MetaMaxCharacters - 16000);
    }
}
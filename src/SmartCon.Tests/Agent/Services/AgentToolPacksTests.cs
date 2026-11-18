using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentToolPacksTests
{
    private static readonly string[] ExpectedPackOrder =
    {
        "views", "view_settings", "view_edit", "templates", "model", "levels", "elements",
        "params", "changes", "plans", "meta", "sheets", "families", "schedules", "filters",
        "worksets", "links", "mep", "annotations", "audit"
    };

    private static readonly string[] ExpectedEmptyPacks =
    {
        "sheets", "families", "schedules", "filters", "worksets", "links", "mep", "annotations", "audit"
    };

    // ADR-075 §2 pins exactly these nine tools.
    private static readonly string[] ExpectedCoreToolNames =
    {
        "get_project_overview", "list_views", "list_levels", "query_elements",
        "find_tools", "execute_plan", "report_tooling_feedback", "rename_views",
        "apply_view_template"
    };

    private static readonly IReadOnlyDictionary<string, string[]> ExpectedToolsByPack =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["views"] = new[] { "list_views", "get_current_view_info", "rename_views" },
            ["view_settings"] = new[]
            {
                "get_view_settings", "set_view_range", "set_view_crop", "set_view_visibility"
            },
            ["view_edit"] = new[] { "configure_views", "copy_view_settings" },
            ["templates"] = new[]
            {
                "list_view_templates", "get_view_template_details", "apply_view_template"
            },
            ["model"] = new[] { "get_project_overview", "list_categories", "get_selected_elements" },
            ["levels"] = new[] { "list_levels", "rename_levels" },
            ["elements"] = new[] { "query_elements", "summarize_elements", "get_element_details" },
            ["params"] = new[] { "set_parameters", "remap_parameter_values" },
            ["changes"] = new[] { "undo_last", "discard_change_set" },
            ["plans"] = new[] { "execute_plan" },
            ["meta"] = new[] { "find_tools", "report_tooling_feedback" }
        };

    [Fact]
    public void Packs_HaveFixedOrderAndCount()
    {
        Assert.Equal(20, AgentToolPacks.All.Count);
        Assert.Equal(ExpectedPackOrder, AgentToolPacks.All.Select(pack => pack.Name).ToArray());
        Assert.Equal(20, AgentToolPacks.All.Select(pack => pack.Name).Distinct().Count());
    }

    [Fact]
    public void EveryPack_HasDescription()
    {
        Assert.All(AgentToolPacks.All, pack =>
            Assert.False(string.IsNullOrWhiteSpace(pack.Description), pack.Name));
    }

    [Fact]
    public void EveryExistingTool_IsInExactlyOnePopulatedPack()
    {
        var placements = AgentToolPacks.All
            .Where(pack => pack.Tools.Count > 0)
            .SelectMany(pack => pack.Tools.Select(tool => (Tool: tool, Pack: pack.Name)))
            .ToArray();

        Assert.Equal(27, placements.Length);
        Assert.All(placements.GroupBy(pair => pair.Tool), group =>
            Assert.Single(group));
        Assert.All(placements, pair =>
            Assert.NotNull(AgentToolPacks.PackOfTool(pair.Tool)));
    }

    [Fact]
    public void PopulatedPacks_MatchTheSpecifiedDistribution()
    {
        foreach (var (packName, expectedTools) in ExpectedToolsByPack)
        {
            Assert.True(AgentToolPacks.TryGet(packName, out var pack), packName);
            Assert.NotNull(pack);
            Assert.Equal(
                expectedTools.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                pack!.Tools.OrderBy(name => name, StringComparer.Ordinal).ToArray());
            Assert.Equal(
                expectedTools.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                AgentToolPacks.ToolsInPack(packName)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray());
        }

        var populated = AgentToolPacks.All
            .Where(pack => pack.Tools.Count > 0)
            .Select(pack => pack.Name)
            .OrderBy(name => name, StringComparer.Ordinal);
        Assert.Equal(
            ExpectedToolsByPack.Keys.OrderBy(name => name, StringComparer.Ordinal),
            populated.ToArray());
    }

    [Fact]
    public void OnlyReservedPacks_AreEmpty()
    {
        var empty = AgentToolPacks.All
            .Where(pack => pack.Tools.Count == 0)
            .Select(pack => pack.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ExpectedEmptyPacks.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            empty);

        foreach (var pack in ExpectedEmptyPacks)
        {
            Assert.Empty(AgentToolPacks.ToolsInPack(pack));
        }
    }

    [Fact]
    public void CoreToolNames_AreExactlyTheSpecifiedNine()
    {
        Assert.Equal(9, AgentToolPacks.CoreToolNames.Count);
        Assert.Equal(
            ExpectedCoreToolNames.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            AgentToolPacks.CoreToolNames.OrderBy(name => name, StringComparer.Ordinal).ToArray());

        Assert.All(AgentToolPacks.CoreToolNames, name =>
        {
            Assert.NotNull(AgentToolPacks.PackOfTool(name));
            Assert.True(AgentToolPacks.IsCore(name), name);
        });

        Assert.False(AgentToolPacks.IsCore("set_view_crop"));
        Assert.False(AgentToolPacks.IsCore("set_view_visibility"));
    }

    [Fact]
    public void EveryCard_PackMatchesPackOwnership()
    {
        Assert.All(AgentToolCards.All, card =>
        {
            var pack = AgentToolPacks.PackOfTool(card.ToolName);
            Assert.NotNull(pack);
            Assert.Equal(card.Pack, pack!.Name);
            Assert.Contains(card.ToolName, pack.Tools);
        });
    }

    [Fact]
    public void UnknownNames_ReturnEmptyResults()
    {
        Assert.False(AgentToolPacks.TryGet("rooms", out var pack));
        Assert.Null(pack);
        Assert.Null(AgentToolPacks.PackOfTool("rooms"));
        Assert.Empty(AgentToolPacks.ToolsInPack("rooms"));
        Assert.False(AgentToolPacks.IsCore("rooms"));
    }
}

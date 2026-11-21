using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentToolPacksTests
{
    private static readonly string[] ExpectedPackOrder =
    {
        "views", "view_settings", "view_edit", "templates", "model", "levels", "elements",
        "params", "changes", "jobs", "plans", "meta", "sheets", "families", "schedules", "filters",
        "worksets", "links", "mep", "annotations", "audit"
    };

    private static readonly string[] ExpectedEmptyPacks =
    {
        "worksets", "links", "mep", "annotations", "audit"
    };

    // ADR-075 §2 pins the research core; the write primitives stay in their packs so
    // they are always loaded together with the pack skill.
    private static readonly string[] ExpectedCoreToolNames =
    {
        "get_project_overview", "list_categories", "list_levels", "list_views",
        "get_current_view_info", "get_view_settings", "query_elements",
        "summarize_elements", "get_element_details", "get_selected_elements", "find_tools"
    };

    private static readonly string[] ExpectedSystemToolNames =
    {
        "load_tool_pack", "read_tool_result", "execute_plan"
    };

    private static readonly IReadOnlyDictionary<string, string[]> ExpectedToolsByPack =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["views"] = new[] { "list_views", "get_current_view_info", "rename_views", "delete_views" },
            ["view_settings"] = new[]
            {
                "get_view_settings", "set_view_range", "set_view_crop", "set_view_visibility",
                "set_view_parameters"
            },
            ["view_edit"] = new[] { "configure_views", "copy_view_settings" },
            ["templates"] = new[]
            {
                "list_view_templates", "get_view_template_details", "apply_view_template",
                "set_view_template_inclusion"
            },
            ["model"] = new[] { "get_project_overview", "list_categories", "get_selected_elements" },
            ["levels"] = new[] { "list_levels", "rename_levels" },
            ["elements"] = new[]
            {
                "query_elements", "summarize_elements", "get_element_details", "delete_elements"
            },
            ["params"] = new[]
            {
                "set_parameters", "remap_parameter_values", "list_project_parameters",
                "add_project_shared_parameter"
            },
            ["changes"] = new[] { "undo_last", "discard_change_set" },
            ["jobs"] = new[]
            {
                "start_job", "get_job_status", "cancel_job",
                "get_job_report", "rollback_job", "retry_failed_items"
            },
            ["plans"] = new[] { "execute_plan" },
            ["meta"] = new[] { "find_tools", "read_tool_result" },
            ["sheets"] = new[]
            {
                "list_sheets", "get_sheet_content", "delete_sheets", "create_sheet",
                "create_viewports", "move_viewport", "set_sheet_number", "rename_sheet",
                "sanitize_sheet_numbers"
            },
            ["schedules"] = new[]
            {
                "list_schedules", "get_schedule_data", "get_schedule_definition",
                "delete_schedules", "create_schedule", "add_schedule_field",
                "set_schedule_filter", "set_schedule_sort", "place_schedule_on_sheet"
            },
            ["filters"] = new[]
            {
                "list_filters", "get_filter_details", "list_filterable_parameters",
                "create_view_filter", "set_filter_rules",
                "rename_filter", "delete_filter", "set_filter_overrides"
            },
            ["families"] = new[]
            {
                "list_families", "list_family_types", "get_family_parameters",
                "find_families_with_parameter", "list_shared_parameters",
                "rename_family_parameter", "add_family_parameter",
                "set_family_parameter_values", "set_family_formula",
                "map_family_parameters", "delete_family_types", "remove_family_parameter",
                "replace_family_parameter"
            }
        };

    [Fact]
    public void Packs_HaveFixedOrderAndCount()
    {
        Assert.Equal(21, AgentToolPacks.All.Count);
        Assert.Equal(ExpectedPackOrder, AgentToolPacks.All.Select(pack => pack.Name).ToArray());
        Assert.Equal(21, AgentToolPacks.All.Select(pack => pack.Name).Distinct().Count());
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

        Assert.Equal(78, placements.Length);
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
            // ToolsInPack also enables the pack bootstrap tools: they belong to
            // another pack but must be callable before the first step (#400).
            Assert.Equal(
                expectedTools
                    .Concat(pack.Bootstrap)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray(),
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
    public void CoreToolNames_AreExactlyTheResearchEleven()
    {
        Assert.Equal(11, AgentToolPacks.CoreToolNames.Count);
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
        // Write primitives are pack tools now: they arrive with the pack skill.
        Assert.False(AgentToolPacks.IsCore("rename_views"));
        Assert.False(AgentToolPacks.IsCore("apply_view_template"));
    }

    [Fact]
    public void SystemToolNames_AreTheHarnessPlumbing()
    {
        Assert.Equal(
            ExpectedSystemToolNames.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            AgentToolPacks.SystemToolNames.OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.All(AgentToolPacks.SystemToolNames, name =>
            Assert.True(AgentToolPacks.IsAlwaysAvailable(name), name));
        Assert.All(AgentToolPacks.CoreToolNames, name =>
            Assert.True(AgentToolPacks.IsAlwaysAvailable(name), name));
        Assert.DoesNotContain(
            AgentToolPacks.SystemToolNames,
            name => AgentToolPacks.IsCore(name));
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

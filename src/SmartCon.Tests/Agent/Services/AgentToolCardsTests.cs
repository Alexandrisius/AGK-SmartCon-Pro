using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentToolCardsTests
{
    private static readonly string[] ExpectedToolNames =
    {
        "get_project_overview",
        "list_views",
        "list_levels",
        "list_categories",
        "get_selected_elements",
        "query_elements",
        "get_element_details",
        "summarize_elements",
        "list_view_templates",
        "get_view_settings",
        "get_view_template_details",
        "get_current_view_info",
        "list_sheets",
        "get_sheet_content",
        "list_schedules",
        "get_schedule_data",
        "rename_views",
        "rename_levels",
        "apply_view_template",
        "set_view_range",
        "set_view_crop",
        "set_view_template_inclusion",
        "set_view_visibility",
        "configure_views",
        "copy_view_settings",
        "create_view_filter",
        "set_filter_overrides",
        "list_families",
        "list_family_types",
        "get_family_parameters",
        "find_families_with_parameter",
        "rename_family_parameter",
        "add_family_parameter",
        "set_family_parameter_values",
        "set_family_formula",
        "map_family_parameters",
        "set_parameters",
        "remap_parameter_values",
        "undo_last",
        "discard_change_set",
        "execute_plan",
        "find_tools",
        "read_tool_result",
        "list_filters",
        "get_filter_details",
        "list_filterable_parameters",
        "set_filter_rules",
        "delete_filter",
        "delete_views",
        "delete_sheets",
        "delete_family_types",
        "remove_family_parameter",
        "set_view_parameters",
        "rename_filter",
        "get_schedule_definition",
        "list_shared_parameters",
        "start_job",
        "get_job_status",
        "cancel_job",
        "get_job_report",
        "rollback_job",
        "retry_failed_items"
    };

    [Fact]
    public void Catalog_HasExactlyOneCardPerExistingTool()
    {
        Assert.Equal(62, AgentToolCards.Count);

        var actual = AgentToolCards.All
            .Select(card => card.ToolName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var expected = ExpectedToolNames
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EveryCard_HasRequiredRetrievalFields()
    {
        Assert.All(AgentToolCards.All, card =>
        {
            Assert.False(string.IsNullOrWhiteSpace(card.Pack), card.ToolName + ": Pack");
            Assert.False(string.IsNullOrWhiteSpace(card.Purpose), card.ToolName + ": Purpose");
            Assert.False(string.IsNullOrWhiteSpace(card.WhenToUse), card.ToolName + ": WhenToUse");
            Assert.False(string.IsNullOrWhiteSpace(card.WhenNotToUse), card.ToolName + ": WhenNotToUse");
            Assert.True(card.Examples.Count >= 3, card.ToolName + ": Examples");
            Assert.True(card.Synonyms.Count >= 8, card.ToolName + ": Synonyms");
            Assert.True(card.NegativeExamples.Count >= 2, card.ToolName + ": NegativeExamples");
            Assert.True(card.TimeoutMs > 0, card.ToolName + ": TimeoutMs");
        });
    }

    [Fact]
    public void EveryCard_StringEntriesAreFilled()
    {
        Assert.All(AgentToolCards.All, card =>
        {
            Assert.All(card.Examples, value => Assert.False(string.IsNullOrWhiteSpace(value)));
            Assert.All(card.Synonyms, value => Assert.False(string.IsNullOrWhiteSpace(value)));
            Assert.All(card.NegativeExamples, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        });
    }

    [Fact]
    public void EveryCard_PointsToAnExistingPack()
    {
        Assert.All(AgentToolCards.All, card =>
        {
            Assert.True(
                AgentToolPacks.TryGet(card.Pack, out var pack),
                card.ToolName + " references unknown pack '" + card.Pack + "'");
            Assert.NotNull(pack);
        });
    }

    [Fact]
    public void RequiredContext_IsASupportedValue()
    {
        Assert.All(AgentToolCards.All, card =>
        {
            if (card.RequiredContext is null)
                return;

            Assert.True(
                card.RequiredContext == AgentToolPacks.ActiveView ||
                card.RequiredContext == AgentToolPacks.Selection,
                card.ToolName + " has unexpected RequiredContext '" + card.RequiredContext + "'");
        });
    }

    [Fact]
    public void TryGet_ReturnsCardByNameAndNullForUnknown()
    {
        Assert.True(AgentToolCards.TryGet("rename_views", out var card));
        Assert.NotNull(card);
        Assert.Equal("views", card!.Pack);

        Assert.False(AgentToolCards.TryGet("unknown_tool", out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void Cards_AreNotMutatedByIdEnrich()
    {
        var before = AgentToolCards.All
            .Select(card => card.Pack + ":" + card.Purpose)
            .ToArray();
        var definition = new AgentToolDefinition
        {
            Name = "list_views",
            Description = "List views",
            InputSchemaJson = @"{""type"":""object"",""properties"":{}}"
        };

        _ = AgentToolCatalog.Enrich(definition);

        var after = AgentToolCards.All
            .Select(card => card.Pack + ":" + card.Purpose)
            .ToArray();
        Assert.Equal(before, after);
    }
}

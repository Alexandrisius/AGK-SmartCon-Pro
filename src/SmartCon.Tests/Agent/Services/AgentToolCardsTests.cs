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
        "create_sheet",
        "create_viewports",
        "move_viewport",
        "move_viewports",
        "remove_viewports",
        "set_sheet_number",
        "renumber_sheets",
        "rename_sheet",
        "sanitize_sheet_numbers",
        "list_schedules",
        "get_schedule_data",
        "rename_views",
        "duplicate_view",
        "create_view",
        "create_view_template",
        "set_browser_folder",
        "list_system_types",
        "replace_family_parameter",
        "rename_levels",
        "apply_view_template",
        "set_view_range",
        "set_view_crop",
        "set_view_template_inclusion",
        "set_view_visibility",
        "configure_views",
        "copy_view_settings",
        "copy_crop_from_view",
        "list_scope_boxes",
        "assign_scope_box",
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
        "duplicate_family_type",
        "rename_family_type",
        "replace_family_type",
        "load_family",
        "purge_families",
        "set_parameters",
        "remap_parameter_values",
        "list_project_parameters",
        "add_project_shared_parameter",
        "undo_last",
        "undo_until",
        "discard_change_set",
        "list_change_sets",
        "get_change_set_details",
        "save_document",
        "audit_model",
        "execute_plan",
        "find_tools",
        "read_tool_result",
        "list_filters",
        "get_filter_details",
        "list_filterable_parameters",
        "set_filter_rules",
        "delete_filter",
        "merge_duplicate_filters",
        "delete_views",
        "delete_sheets",
        "delete_elements",
        "delete_schedules",
        "create_schedule",
        "add_schedule_field",
        "set_schedule_filter",
        "set_schedule_sort",
        "place_schedule_on_sheet",
        "remove_schedule_field",
        "set_schedule_field_order",
        "set_schedule_field_visibility",
        "set_schedule_totals",
        "move_schedule_on_sheet",
        "set_mep_system_type",
        "get_browser_organization",
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
        "retry_failed_items",
        "list_mep_systems",
        "list_materials",
        "semantic_match",
        "read_classifier",
        "get_element_relations",
        "trace_system_chain",
        "get_room_contents",
        "group_families_by_shared_parameters",
        "audit_fill_rate",
        "list_type_parameter_candidates",
        "create_floor_plans",
        "create_views_batch",
        "rename_schedule",
        "set_schedule_field_format",
        "set_schedule_style",
        "remove_schedule_from_sheet",
        "clone_schedule",
        "get_titleblock_options",
        "get_view_placement_preview",
        "build_system_sheets",
        "open_model_background",
        "close_model",
        "list_known_models",
        "list_classification_entries",
        "load_classification_table",
        "classify_types",
        "set_shared_parameter_file",
        "get_document_status",
        "get_element_bounds"
    };

    [Fact]
    public void Catalog_HasExactlyOneCardPerExistingTool()
    {
        Assert.Equal(136, AgentToolCards.Count);

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
    public void BatchAnalogue_OnlyOnPerElementWriteToolsWithARealJobAction()
    {
        var expectedBatchedTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "set_parameters",
            "remap_parameter_values",
            "rename_views",
            "map_family_parameters",
            "replace_family_parameter"
        };
        var actualBatchedTools = AgentToolCards.All
            .Where(card => card.BatchAnalogue is not null)
            .Select(card => card.ToolName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expectedBatchedTools, actualBatchedTools);

        Assert.All(AgentToolCards.All, card =>
        {
            if (card.BatchAnalogue is null)
                return;

            Assert.Matches(
                "^start_job\\(action:\"(rename_views|set_parameters|map_family_parameters|replace_family_parameter)\"\\)$",
                card.BatchAnalogue);
            // #430: replace_family_parameter is a single-shot write (one parameter of
            // one family per call, SupportsBulk=false), so its card stays Bulk=false —
            // the batch path is the job action itself, not the tool.
            if (card.ToolName == "replace_family_parameter")
                return;

            Assert.True(card.Bulk, card.ToolName + ": BatchAnalogue requires a batch write card");
        });
    }

    [Fact]
    public void ScheduleCards_DescribeDiagnosticBlocks()
    {
        // #450/#452: routing cards must advertise the new payload blocks, or the
        // model will not know list_schedules surfaces empty schedules and
        // duplicates, and that set_schedule_filter previews the row estimate.
        Assert.True(AgentToolCards.TryGet("list_schedules", out var list));
        Assert.NotNull(list);
        Assert.Contains("emptySchedules", list!.Purpose, StringComparison.Ordinal);
        Assert.Contains("duplicateGroups", list.Purpose, StringComparison.Ordinal);

        Assert.True(AgentToolCards.TryGet("set_schedule_filter", out var filter));
        Assert.NotNull(filter);
        Assert.Contains("estimatedRowsAfter", filter!.Purpose, StringComparison.Ordinal);
    }

    [Fact]
    public void AddFamilyParameterCard_NamesTheSharedParameterFileToolWithItsPack()
    {
        // S1-R3: createDefinition:true needs a shared parameter file; the card
        // must point at the tool that actually exists in the catalog (pack
        // params), not at a bare tool name.
        Assert.True(AgentToolCards.TryGet("add_family_parameter", out var card));
        Assert.NotNull(card);
        Assert.Contains(
            "set_shared_parameter_file пака params",
            card!.Purpose,
            StringComparison.Ordinal);
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

using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class LoadToolPackToolTests
{
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static AgentToolContext Context() =>
        new(new ImmediateAwaitableEvent(), "doc", Guid.Empty);

    private static LoadToolPackTool CreateTool(
        AgentSessionContext session,
        FakeToolRegistry registry)
    {
        var tool = new LoadToolPackTool(session);
        tool.Attach(registry);
        return tool;
    }

    private static FakeToolRegistry ViewsRegistry() =>
        new(
            new ScriptedTool("list_views"),
            new ScriptedTool("get_current_view_info"),
            new ScriptedTool("rename_views"),
            new ScriptedTool("apply_view_template"));

    [Fact]
    public async Task Execute_ByPack_LoadsEveryToolAndQueuesIt()
    {
        var session = new AgentSessionContext();
        var tool = CreateTool(session, ViewsRegistry());

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"pack\":\"views\"}"), CancellationToken.None);

        Assert.True(result.Success);
        var data = result.Data!.Value;
        var loaded = data.GetProperty("loaded").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();
        Assert.Equal(8, loaded.Count);
        Assert.Contains("rename_views", loaded);
        Assert.Contains("delete_views", loaded);
        Assert.Equal("views", data.GetProperty("pack").GetString());
        Assert.Equal(8, data.GetProperty("totalTools").GetInt32());
        Assert.Equal("next_round", data.GetProperty("appliedTo").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("hint").GetString()));

        Assert.Equal(8, session.PendingToolNames.Count);
        Assert.Equal(8, session.LoadedToolNames.Count);
        Assert.Contains("get_current_view_info", session.PendingToolNames);
    }

    [Fact]
    public async Task Execute_ByPacksArray_LoadsAllPacksInOneCall()
    {
        var session = new AgentSessionContext();
        var tool = CreateTool(
            session,
            new FakeToolRegistry(
                new ScriptedTool("list_views"),
                new ScriptedTool("get_current_view_info"),
                new ScriptedTool("rename_views"),
                new ScriptedTool("list_levels"),
                new ScriptedTool("rename_levels")));

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"packs\":[\"views\",\"levels\"]}"),
            CancellationToken.None);

        Assert.True(result.Success);
        var loaded = result.Data!.Value.GetProperty("loaded").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();
        Assert.Contains("rename_views", loaded);
        Assert.Contains("rename_levels", loaded);
        Assert.Equal(10, session.PendingToolNames.Count);
    }

    [Fact]
    public async Task Execute_ByTools_LoadsExplicitNames()
    {
        var session = new AgentSessionContext();
        var tool = CreateTool(session, ViewsRegistry());

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"tools\":[\"rename_views\",\"apply_view_template\"]}"),
            CancellationToken.None);

        Assert.True(result.Success);
        var loaded = result.Data!.Value.GetProperty("loaded").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("next_round", result.Data.Value.GetProperty("appliedTo").GetString());
        Assert.Contains("apply_view_template", session.LoadedToolNames);
    }

    [Fact]
    public async Task Execute_UnknownTool_ReturnsSuggestions()
    {
        var session = new AgentSessionContext();
        var tool = CreateTool(session, ViewsRegistry());

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"tools\":[\"rename_view\"]}"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("unknown_tool", result.ErrorCode);
        var unknown = result.Data!.Value.GetProperty("unknown");
        Assert.Equal(1, unknown.GetArrayLength());
        Assert.Equal("rename_view", unknown[0].GetProperty("name").GetString());
        var suggestions = unknown[0].GetProperty("suggestions").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();
        Assert.InRange(suggestions.Count, 1, 3);
        Assert.Contains("rename_views", suggestions);
        Assert.Empty(session.PendingToolNames);
        Assert.Empty(session.LoadedToolNames);
    }

    [Fact]
    public async Task Execute_SecondCall_AddsInsteadOfReplacing()
    {
        var session = new AgentSessionContext();
        var tool = CreateTool(session, ViewsRegistry());

        await tool.ExecuteAsync(
            Context(), Parse("{\"pack\":\"views\"}"), CancellationToken.None);
        var second = await tool.ExecuteAsync(
            Context(), Parse("{\"tools\":[\"apply_view_template\"]}"), CancellationToken.None);

        Assert.True(second.Success);
        var data = second.Data!.Value;
        var loaded = data.GetProperty("loaded").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();
        Assert.Equal(new[] { "apply_view_template" }, loaded);
        Assert.Equal(9, data.GetProperty("totalTools").GetInt32());
        Assert.Equal("next_round", data.GetProperty("appliedTo").GetString());
        Assert.Equal(9, session.LoadedToolNames.Count);
    }

    [Fact]
    public async Task Execute_AllAlreadyLoaded_ReportsNone()
    {
        var session = new AgentSessionContext();
        var tool = CreateTool(session, ViewsRegistry());

        await tool.ExecuteAsync(
            Context(), Parse("{\"pack\":\"views\"}"), CancellationToken.None);

        // The next request actually carries the loaded tools: only then are they
        // "already loaded" from the model's point of view.
        session.ActiveToolNames = new[]
        {
            "list_views", "get_current_view_info", "rename_views", "delete_views",
            "duplicate_view", "create_view", "set_browser_folder",
            "get_browser_organization"
        };

        var second = await tool.ExecuteAsync(
            Context(), Parse("{\"pack\":\"views\"}"), CancellationToken.None);

        Assert.True(second.Success);
        var data = second.Data!.Value;
        Assert.Equal(0, data.GetProperty("loaded").GetArrayLength());
        Assert.Equal(8, data.GetProperty("alreadyLoaded").GetArrayLength());
        Assert.Equal("none", data.GetProperty("appliedTo").GetString());
        Assert.Equal(8, session.PendingToolNames.Count);
    }

    [Fact]
    public async Task Execute_LoadedButNotInTheCurrentRequest_IsQueuedAgain()
    {
        // Regression: the tool was loaded earlier, but the request cap pushed it
        // out of the request. Reporting "already loaded" here left the model
        // unable to ever call it again.
        var session = new AgentSessionContext();
        session.TouchTool("undo_last");
        session.ActiveToolNames = new[] { "list_views" };
        var tool = CreateTool(session, new FakeToolRegistry(new ScriptedTool("undo_last")));

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"tools\":[\"undo_last\"]}"), CancellationToken.None);

        Assert.True(result.Success);
        var data = result.Data!.Value;
        var loaded = data.GetProperty("loaded").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();
        Assert.Equal(new[] { "undo_last" }, loaded);
        Assert.Equal("next_round", data.GetProperty("appliedTo").GetString());
        Assert.Contains("undo_last", session.PendingToolNames);
        Assert.Equal("undo_last", session.LoadedToolNames[session.LoadedToolNames.Count - 1]);
    }

    [Fact]
    public async Task Execute_WithoutPackOrTools_IsRejected()
    {
        var session = new AgentSessionContext();
        var tool = CreateTool(session, ViewsRegistry());

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"context\":\"only context\"}"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
    }

    [Fact]
    public async Task Execute_UnknownPack_IsRejected()
    {
        var session = new AgentSessionContext();
        var tool = CreateTool(session, ViewsRegistry());

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"pack\":\"rooms\"}"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
    }

    [Fact]
    public async Task Execute_ReportsRemainingAvailableTools()
    {
        var session = new AgentSessionContext();
        var tool = CreateTool(
            session,
            new FakeToolRegistry(
                new ScriptedTool("list_views"),
                new ScriptedTool("get_current_view_info"),
                new ScriptedTool("rename_views"),
                new ScriptedTool("list_filters")));

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"pack\":\"views\"}"), CancellationToken.None);

        Assert.True(result.Success);
        var data = result.Data!.Value;
        Assert.Equal(1, data.GetProperty("remainingAvailable").GetInt32());
        Assert.Equal(
            1,
            data.GetProperty("remainingByPack").GetProperty("filters").GetInt32());
    }

    [Fact]
    public async Task Execute_CountersAddUpToTheCatalogAndSchemasAreReturned()
    {
        // External run 6, P2: totalTools (loaded) + remainingAvailable did not add
        // up to the catalog because core tools were counted nowhere. The payload now
        // spells the equation out: catalog = core + loaded + inRequest + remaining.
        var session = new AgentSessionContext();
        var tools = AgentToolPacks.CoreToolNames
            .Select(name => (IAgentTool)new ScriptedTool(name))
            .ToList();
        tools.Add(new ScriptedTool("apply_view_template"));
        tools.Add(new ScriptedTool("list_view_templates"));
        tools.Add(new ScriptedTool("get_view_template_details"));
        tools.Add(new ScriptedTool("set_view_template_inclusion"));
        var registry = new FakeToolRegistry(tools.ToArray());
        var tool = CreateTool(session, registry);

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"pack\":\"templates\"}"), CancellationToken.None);

        Assert.True(result.Success);
        var data = result.Data!.Value;
        var catalogTotal = data.GetProperty("catalogTotal").GetInt32();
        var core = data.GetProperty("coreTools").GetInt32();
        var loaded = data.GetProperty("loadedTools").GetInt32();
        var inRequest = data.GetProperty("inRequestTools").GetInt32();
        var remaining = data.GetProperty("remainingAvailable").GetInt32();

        Assert.Equal(catalogTotal, core + loaded + inRequest + remaining);
        // apply_view_template is a pack tool now: enabling the templates pack queues it
        // together with its three siblings, so all four count as loaded.
        Assert.Equal(4, loaded);
        Assert.Equal(AgentToolPacks.CoreToolNames.Count, core);
        Assert.Equal(0, remaining);
        Assert.Equal(
            "catalogTotal = coreTools + loadedTools + inRequestTools + remainingAvailable",
            data.GetProperty("counting").GetString());

        var schemas = data.GetProperty("schemas");
        Assert.Equal(4, schemas.GetArrayLength());
        Assert.Equal("apply_view_template", schemas[0].GetProperty("name").GetString());
        Assert.Equal(
            "object",
            schemas[0].GetProperty("inputSchema").GetProperty("type").GetString());
        Assert.False(data.GetProperty("schemasTruncated").GetBoolean());
    }

    [Fact]
    public async Task Execute_WithoutAttachedRegistry_FailsClosed()
    {
        var tool = new LoadToolPackTool(new AgentSessionContext());

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"pack\":\"views\"}"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("tool_failed", result.ErrorCode);
    }

    private static FakeToolRegistry FiltersRegistry() =>
        new(
            new ScriptedTool("get_view_settings"),
            new ScriptedTool("list_filters"),
            new ScriptedTool("get_filter_details"),
            new ScriptedTool("list_filterable_parameters"),
            new ScriptedTool("create_view_filter"),
            new ScriptedTool("set_filter_rules"),
            new ScriptedTool("rename_filter"),
            new ScriptedTool("delete_filter"),
            new ScriptedTool("set_filter_overrides"));

    [Fact]
    public async Task Execute_FiltersPack_ReturnsSkillAndBootstrapViewSettings()
    {
        var session = new AgentSessionContext();
        var tool = CreateTool(session, FiltersRegistry());

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"pack\":\"filters\"}"), CancellationToken.None);

        Assert.True(result.Success);
        var data = result.Data!.Value;
        var skills = data.GetProperty("skills").EnumerateArray().ToList();
        Assert.Single(skills);
        Assert.Equal("filters", skills[0].GetProperty("pack").GetString());
        var skill = skills[0].GetProperty("markdown").GetString();
        Assert.False(string.IsNullOrWhiteSpace(skill));
        Assert.Contains("SetIsFilterEnabled", skill);
        Assert.Contains("SetFilterVisibility", skill);
        Assert.Contains("visibility", skill);

        var loaded = data.GetProperty("loaded").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();
        Assert.Contains("list_filterable_parameters", loaded);
        Assert.Contains("get_view_settings", loaded);
        Assert.Contains("get_view_settings", session.PendingToolNames);
    }

    [Fact]
    public async Task Execute_TwoPacksWithSkills_ReturnsBothSkills()
    {
        var session = new AgentSessionContext();
        var tool = CreateTool(
            session,
            new FakeToolRegistry(
                new ScriptedTool("get_view_settings"),
                new ScriptedTool("list_filters"),
                new ScriptedTool("list_families"),
                new ScriptedTool("list_family_types")));

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"packs\":[\"filters\",\"families\"]}"),
            CancellationToken.None);

        Assert.True(result.Success);
        var skills = result.Data!.Value.GetProperty("skills").EnumerateArray()
            .Select(item => item.GetProperty("pack").GetString()!)
            .ToList();
        Assert.Equal(2, skills.Count);
        Assert.Contains("filters", skills);
        Assert.Contains("families", skills);
        Assert.Equal(2, session.SentPackSkills.Count);
    }

    [Fact]
    public async Task Execute_ExplicitTool_DeliversTheSkillOfItsPack()
    {
        // The model may enable one tool by name instead of the whole pack: the manual
        // of the owning pack must still arrive, otherwise it writes without the rules.
        var session = new AgentSessionContext();
        var tool = CreateTool(session, new FakeToolRegistry(new ScriptedTool("list_families")));

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"tools\":[\"list_families\"]}"), CancellationToken.None);

        Assert.True(result.Success);
        var skills = result.Data!.Value.GetProperty("skills").EnumerateArray().ToList();
        Assert.Single(skills);
        Assert.Equal("families", skills[0].GetProperty("pack").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            skills[0].GetProperty("markdown").GetString()));
    }

    [Fact]
    public async Task Execute_FiltersPackTwice_SendsTheSkillOnlyOnce()
    {
        var session = new AgentSessionContext();
        var tool = CreateTool(session, FiltersRegistry());

        await tool.ExecuteAsync(
            Context(), Parse("{\"pack\":\"filters\"}"), CancellationToken.None);
        var second = await tool.ExecuteAsync(
            Context(), Parse("{\"pack\":\"filters\"}"), CancellationToken.None);

        Assert.True(second.Success);
        var data = second.Data!.Value;
        Assert.Equal(JsonValueKind.Null, data.GetProperty("skills").ValueKind);
        var alreadySent = data.GetProperty("skillAlreadySent").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();
        Assert.Contains("filters", alreadySent);
    }

    [Fact]
    public async Task Execute_PackWithoutSkill_ReturnsNoSkillsEntry()
    {
        var session = new AgentSessionContext();
        var tool = CreateTool(session, ViewsRegistry());

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"pack\":\"worksets\"}"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("pack_reserved_empty", result.ErrorCode);
    }
}

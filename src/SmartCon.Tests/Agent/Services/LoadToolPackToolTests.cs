using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
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
        Assert.Equal(3, loaded.Count);
        Assert.Contains("rename_views", loaded);
        Assert.Equal("views", data.GetProperty("pack").GetString());
        Assert.Equal(3, data.GetProperty("totalTools").GetInt32());
        Assert.Equal("next_round", data.GetProperty("appliedTo").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("hint").GetString()));

        Assert.Equal(3, session.PendingToolNames.Count);
        Assert.Equal(3, session.LoadedToolNames.Count);
        Assert.Contains("get_current_view_info", session.PendingToolNames);
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
        Assert.Equal(4, data.GetProperty("totalTools").GetInt32());
        Assert.Equal("next_round", data.GetProperty("appliedTo").GetString());
        Assert.Equal(4, session.LoadedToolNames.Count);
    }

    [Fact]
    public async Task Execute_AllAlreadyLoaded_ReportsNone()
    {
        var session = new AgentSessionContext();
        var tool = CreateTool(session, ViewsRegistry());

        await tool.ExecuteAsync(
            Context(), Parse("{\"pack\":\"views\"}"), CancellationToken.None);
        var second = await tool.ExecuteAsync(
            Context(), Parse("{\"pack\":\"views\"}"), CancellationToken.None);

        Assert.True(second.Success);
        var data = second.Data!.Value;
        Assert.Equal(0, data.GetProperty("loaded").GetArrayLength());
        Assert.Equal(3, data.GetProperty("alreadyLoaded").GetArrayLength());
        Assert.Equal("none", data.GetProperty("appliedTo").GetString());
        Assert.Equal(3, session.PendingToolNames.Count);
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
    public async Task Execute_WithoutAttachedRegistry_FailsClosed()
    {
        var tool = new LoadToolPackTool(new AgentSessionContext());

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"pack\":\"views\"}"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("tool_failed", result.ErrorCode);
    }
}

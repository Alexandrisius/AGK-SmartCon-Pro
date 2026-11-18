using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class FindToolsToolTests
{
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static AgentToolContext Context() =>
        new(new ImmediateAwaitableEvent(), "doc", Guid.Empty);

    private static FindToolsTool CreateTool(IToolRegistry registry)
    {
        var tool = new FindToolsTool(new RecordingToolRouter());
        tool.Attach(registry);
        return tool;
    }

    [Fact]
    public async Task FindTools_UsesConfiguredRouterAndPassesContext()
    {
        var write = new ScriptedTool("write_tool");
        var read = new ScriptedTool("read_tool", AgentToolRisk.Read, supportsDryRun: false);
        var registry = new FakeToolRegistry(write, read);
        var router = new RecordingToolRouter
        {
            Result = new ToolRoutingDecision { Tools = new[] { write.Definition } }
        };
        var tool = new FindToolsTool(router);
        tool.Attach(registry);

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"query\":\"переименуй\",\"context\":\"нашел 3 вида SC_Eval\"}"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(router.LastQuery);
        Assert.Contains("переименуй", router.LastQuery);
        Assert.Contains("нашел 3 вида SC_Eval", router.LastQuery);
        Assert.Equal(6, router.LastMaxTools);
        var tools = result.Data!.Value.GetProperty("tools");
        Assert.True(tools.GetArrayLength() >= 1);
        Assert.Contains(tools.EnumerateArray(), tool =>
            string.Equals(
                tool.GetProperty("name").GetString(), "write_tool", StringComparison.Ordinal));
        Assert.Contains(tools.EnumerateArray(), tool =>
            tool.GetProperty("risk").GetString() == "Write");
    }

    [Fact]
    public async Task FindTools_CapsContextLength()
    {
        var write = new ScriptedTool("write_tool");
        var router = new RecordingToolRouter
        {
            Result = new ToolRoutingDecision { Tools = new[] { write.Definition } }
        };
        var tool = new FindToolsTool(router);
        tool.Attach(new FakeToolRegistry(write));
        var longContext = new string('x', 5000);

        await tool.ExecuteAsync(
            Context(),
            Parse("{\"query\":\"q\",\"context\":\"" + longContext + "\"}"),
            CancellationToken.None);

        Assert.NotNull(router.LastQuery);
        Assert.True(router.LastQuery!.Length < 2000);
    }

    [Fact]
    public async Task FindTools_WithoutQuery_ReturnsACompactListing()
    {
        var write = new ScriptedTool("write_tool");
        var tool = new FindToolsTool(new RecordingToolRouter());
        tool.Attach(new FakeToolRegistry(write));

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"limit\":5}"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("listing", result.Data!.Value.GetProperty("mode").GetString());
        Assert.True(result.Data.Value.GetProperty("tools").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task FindToolsV2_DefaultLimitIsFive()
    {
        var registry = new AgentToolRegistry(new IAgentTool[]
        {
            new ScriptedTool("list_views"),
            new ScriptedTool("get_current_view_info"),
            new ScriptedTool("get_view_settings"),
            new ScriptedTool("list_view_templates"),
            new ScriptedTool("get_view_template_details"),
            new ScriptedTool("apply_view_template"),
            new ScriptedTool("configure_views"),
            new ScriptedTool("copy_view_settings"),
            new ScriptedTool("set_view_range"),
            new ScriptedTool("set_view_crop")
        });
        var tool = CreateTool(registry);

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"query\":\"настройки вида\"}"), CancellationToken.None);

        Assert.True(result.Success);
        var tools = result.Data!.Value.GetProperty("tools");
        Assert.InRange(tools.GetArrayLength(), 1, 5);
        Assert.True(result.Data.Value.TryGetProperty("packs", out _));
    }

    [Fact]
    public async Task FindToolsV2_PackFilter_ReturnsOnlyThatPack()
    {
        var registry = new AgentToolRegistry(new IAgentTool[]
        {
            new ScriptedTool("set_view_crop"),
            new ScriptedTool("rename_views")
        });
        var tool = CreateTool(registry);

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"query\":\"подрезка области\",\"pack\":\"view_settings\"}"),
            CancellationToken.None);

        Assert.True(result.Success);
        var tools = result.Data!.Value.GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("set_view_crop", tools[0].GetProperty("name").GetString());
        Assert.Equal("view_settings", tools[0].GetProperty("pack").GetString());
    }

    [Fact]
    public async Task FindToolsV2_Listing_ReturnsCompactCardsWithoutSchema()
    {
        var registry = new AgentToolRegistry(new IAgentTool[]
        {
            new ScriptedTool("list_views"),
            new ScriptedTool("get_current_view_info"),
            new ScriptedTool("rename_views"),
            new ScriptedTool("apply_view_template")
        });
        var tool = CreateTool(registry);

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"pack\":\"views\",\"fields\":[\"name\",\"description\"],\"limit\":10}"),
            CancellationToken.None);

        Assert.True(result.Success);
        var data = result.Data!.Value;
        Assert.Equal("listing", data.GetProperty("mode").GetString());
        var tools = data.GetProperty("tools");
        Assert.Equal(3, tools.GetArrayLength());
        foreach (var entry in tools.EnumerateArray())
        {
            Assert.False(entry.TryGetProperty("inputSchema", out _));
            Assert.True(entry.TryGetProperty("name", out _));
            Assert.True(entry.TryGetProperty("description", out _));
            Assert.True(entry.TryGetProperty("pack", out _));
            Assert.True(entry.TryGetProperty("risk", out _));
        }

        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("hint").GetString()));
    }

    [Fact]
    public async Task FindToolsV2_RegularPayload_ContainsCardFieldsAndHint()
    {
        var registry = new AgentToolRegistry(new IAgentTool[]
        {
            new ScriptedTool("get_view_template_details"),
            new ScriptedTool("get_view_settings")
        });
        var tool = CreateTool(registry);

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"query\":\"что контролирует шаблон вида\"}"),
            CancellationToken.None);

        Assert.True(result.Success);
        var data = result.Data!.Value;
        var tools = data.GetProperty("tools");
        Assert.True(tools.GetArrayLength() >= 1);

        var first = tools[0];
        Assert.Equal("get_view_template_details", first.GetProperty("name").GetString());
        Assert.True(first.TryGetProperty("inputSchema", out _));
        Assert.True(first.TryGetProperty("purpose", out _));
        Assert.True(first.TryGetProperty("whenToUse", out _));
        Assert.True(first.TryGetProperty("whenNotToUse", out _));
        Assert.True(first.TryGetProperty("examples", out _));
        Assert.True(first.TryGetProperty("synonyms", out _));
        Assert.True(first.TryGetProperty("timeoutMs", out _));
        Assert.Equal("templates", first.GetProperty("pack").GetString());

        Assert.Contains("load_tool_pack", data.GetProperty("hint").GetString());
        Assert.Contains(
            "templates",
            data.GetProperty("packs").EnumerateArray().Select(pack => pack.GetString()!));
    }

    [Fact]
    public async Task FindToolsV2_UnknownPack_IsRejected()
    {
        var registry = new AgentToolRegistry(new IAgentTool[]
        {
            new ScriptedTool("set_view_crop")
        });
        var tool = CreateTool(registry);

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"query\":\"подрезка\",\"pack\":\"rooms\"}"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
    }

    [Fact]
    public async Task FindToolsV2_UsesDecisionRouterWithoutCoreNames()
    {
        var registry = new AgentToolRegistry(new IAgentTool[]
        {
            new ScriptedTool("set_view_crop"),
            new ScriptedTool("list_views")
        });
        var router = new RecordingToolRouter
        {
            Result = new ToolRoutingDecision
            {
                Tools = new[] { registry.GetDefinitions()[0], registry.GetDefinitions()[1] }
            }
        };
        var tool = new FindToolsTool(router);
        tool.Attach(registry);

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"query\":\"подрезка\"}"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(6, router.LastMaxTools);
        var names = result.Data!.Value.GetProperty("tools").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString())
            .ToList();
        Assert.DoesNotContain("list_views", names);
        Assert.Contains("set_view_crop", names);
    }
}

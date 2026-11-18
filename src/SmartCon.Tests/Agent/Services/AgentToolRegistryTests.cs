using Xunit;
using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentToolRegistryTests
{
    [Fact]
    public void Constructor_InvalidSchema_ThrowsWithToolName()
    {
        var tool = new StubTool("broken_schema", "{\"type\":\"object\"");

        var error = Assert.Throws<InvalidOperationException>(
            () => new AgentToolRegistry(new[] { tool }));

        Assert.Contains("broken_schema", error.Message);
        Assert.Contains("invalid InputSchemaJson", error.Message);
    }

    [Fact]
    public void Constructor_SchemaNotAnObject_Throws()
    {
        var tool = new StubTool("array_schema", "[]");

        var error = Assert.Throws<InvalidOperationException>(
            () => new AgentToolRegistry(new[] { tool }));

        Assert.Contains("array_schema", error.Message);
        Assert.Contains("JSON object", error.Message);
    }

    [Fact]
    public void Constructor_DuplicateNames_Throws()
    {
        var first = new StubTool("same_name", "{\"type\":\"object\"}");
        var second = new StubTool("same_name", "{\"type\":\"object\"}");

        var error = Assert.Throws<InvalidOperationException>(
            () => new AgentToolRegistry(new[] { first, second }));

        Assert.Contains("same_name", error.Message);
    }

    [Fact]
    public void Constructor_ValidTools_RegistersByName()
    {
        var registry = new AgentToolRegistry(new[]
        {
            new StubTool("get_one", "{\"type\":\"object\"}"),
            new StubTool("get_two", "{\"type\":\"object\",\"properties\":{}}")
        });

        Assert.Equal(2, registry.GetAll().Count);
        Assert.True(registry.TryGet("GET_ONE", out var tool));
        Assert.NotNull(tool);
        Assert.False(registry.TryGet("missing", out _));
    }

    private sealed class StubTool : IAgentTool
    {
        public StubTool(string name, string schema)
        {
            Definition = new AgentToolDefinition
            {
                Name = name,
                Description = "stub",
                Risk = AgentToolRisk.Read,
                InputSchemaJson = schema
            };
        }

        public AgentToolDefinition Definition { get; }

        public Task<ToolResult> ExecuteAsync(
            AgentToolContext context,
            JsonElement args,
            CancellationToken ct) => Task.FromResult(ToolResult.Ok(null, "ok"));
    }
}

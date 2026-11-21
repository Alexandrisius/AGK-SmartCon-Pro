using Xunit;
using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
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

    [Fact]
    public void Constructor_EnrichesEveryCatalogCard()
    {
        var tools = AgentToolCards.All
            .Select(card => new StubTool(card.ToolName, "{\"type\":\"object\",\"properties\":{}}"))
            .ToArray();

        var registry = new AgentToolRegistry(tools);

        Assert.Equal(71, registry.GetDefinitions().Count);
        Assert.All(registry.GetDefinitions(), definition =>
        {
            Assert.True(AgentToolCards.TryGet(definition.Name, out var card));
            Assert.NotNull(card);
            Assert.Equal(card!.Pack, definition.Pack);
            Assert.Equal(card.Purpose, definition.Purpose);
            Assert.Equal(card.WhenToUse, definition.WhenToUse);
            Assert.Equal(card.Examples.ToArray(), definition.Examples.ToArray());
            Assert.Equal(card.Synonyms.ToArray(), definition.Synonyms.ToArray());
            Assert.Equal(card.TimeoutMs, definition.TimeoutMs);
            Assert.Equal(definition.SupportsBulk, definition.Bulk);
            Assert.False(string.IsNullOrWhiteSpace(definition.Category));
        });

        var rename = registry.GetDefinitions().Single(definition => definition.Name == "rename_views");
        Assert.Equal("Write", rename.Category);
        Assert.True(rename.SupportsBulk);
        Assert.Equal("views", rename.Pack);
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

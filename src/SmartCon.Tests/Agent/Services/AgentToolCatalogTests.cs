using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentToolCatalogTests
{
    private static AgentToolDefinition BareDefinition(string name) =>
        new()
        {
            Name = name,
            Description = "description of " + name,
            Risk = AgentToolRisk.Read,
            InputSchemaJson = @"{""type"":""object"",""properties"":{}}"
        };

    [Fact]
    public void Enrich_FillsCardFieldsForEveryCard()
    {
        Assert.All(AgentToolCards.All, card =>
        {
            var enriched = AgentToolCatalog.Enrich(BareDefinition(card.ToolName));

            Assert.Equal(card.Pack, enriched.Pack);
            Assert.Equal(card.Purpose, enriched.Purpose);
            Assert.Equal(card.WhenToUse, enriched.WhenToUse);
            Assert.Equal(card.WhenNotToUse, enriched.WhenNotToUse);
            Assert.Equal(card.Examples.ToArray(), enriched.Examples.ToArray());
            Assert.Equal(card.Synonyms.ToArray(), enriched.Synonyms.ToArray());
            Assert.Equal(card.NegativeExamples.ToArray(), enriched.NegativeExamples.ToArray());
            Assert.Equal(card.Idempotent, enriched.Idempotent);
            Assert.Equal(card.TimeoutMs, enriched.TimeoutMs);
            Assert.Equal(card.RequiredContext, enriched.RequiredContext);
            Assert.Equal(card.Bulk, enriched.Bulk);
            Assert.Equal(enriched.SupportsBulk, enriched.Bulk);
        });
    }

    [Fact]
    public void Enrich_PreservesLegacyCategoryTagsAndBulk()
    {
        var listViews = AgentToolCatalog.Enrich(BareDefinition("list_views"));
        Assert.Equal("Read", listViews.Category);
        Assert.Equal(
            new[]
            {
                "виды", "вид", "план", "разрез", "список", "перечень", "views", "list", "plans",
                "папка", "браузер", "организация", "folder", "browser", "organization"
            },
            listViews.Tags.ToArray());
        Assert.False(listViews.SupportsBulk);

        var renameViews = AgentToolCatalog.Enrich(BareDefinition("rename_views"));
        Assert.Equal("Write", renameViews.Category);
        Assert.Equal(
            new[]
            {
                "переименовать", "переименуй", "имя", "название", "rename", "view name"
            },
            renameViews.Tags.ToArray());
        Assert.True(renameViews.SupportsBulk);

        var overview = AgentToolCatalog.Enrich(BareDefinition("get_project_overview"));
        Assert.Equal("Read", overview.Category);
        Assert.Equal(
            new[]
            {
                "проект", "обзор", "сводка", "сколько", "итоги", "overview", "project", "summary"
            },
            overview.Tags.ToArray());
        Assert.False(overview.SupportsBulk);
    }

    [Fact]
    public void Enrich_KeepsExplicitCategoryAndTags()
    {
        var definition = BareDefinition("rename_views");
        definition = new AgentToolDefinition
        {
            Name = definition.Name,
            Description = definition.Description,
            InputSchemaJson = definition.InputSchemaJson,
            Category = "Custom",
            Tags = new[] { "custom-tag" },
            SupportsBulk = true
        };

        var enriched = AgentToolCatalog.Enrich(definition);

        Assert.Equal("Custom", enriched.Category);
        Assert.Equal(new[] { "custom-tag" }, enriched.Tags.ToArray());
        Assert.True(enriched.SupportsBulk);
        Assert.True(enriched.Bulk);
    }

    [Fact]
    public void Enrich_DoesNotLetCardBulkOverrideLegacyBulk()
    {
        var definition = BareDefinition("undo_last");
        definition = new AgentToolDefinition
        {
            Name = definition.Name,
            Description = definition.Description,
            InputSchemaJson = definition.InputSchemaJson,
            SupportsBulk = true
        };

        var enriched = AgentToolCatalog.Enrich(definition);

        Assert.True(enriched.SupportsBulk);
        Assert.True(enriched.Bulk);
        Assert.Equal("changes", enriched.Pack);
    }

    [Fact]
    public void Enrich_UnknownTool_ReturnsSameInstance()
    {
        var definition = BareDefinition("not_a_tool");

        var enriched = AgentToolCatalog.Enrich(definition);

        Assert.Same(definition, enriched);
        Assert.Empty(enriched.Pack);
        Assert.Empty(enriched.Purpose);
        Assert.Equal(30000, enriched.TimeoutMs);
    }

    [Fact]
    public void BulkAlias_IsSynchronizedWithSupportsBulk()
    {
        var enabled = new AgentToolDefinition
        {
            Name = "tool",
            Description = "description",
            InputSchemaJson = @"{""type"":""object""}",
            Bulk = true
        };

        Assert.True(enabled.SupportsBulk);
        Assert.True(enabled.Bulk);

        var disabled = new AgentToolDefinition
        {
            Name = "tool",
            Description = "description",
            InputSchemaJson = @"{""type"":""object""}",
            SupportsBulk = true
        };

        Assert.True(disabled.Bulk);
    }

    [Fact]
    public void Definition_WithCardFields_RoundTripsThroughJson()
    {
        var card = AgentToolCards.All.Single(item => item.ToolName == "set_view_crop");
        var definition = AgentToolCatalog.Enrich(BareDefinition("set_view_crop"));

        var json = JsonSerializer.Serialize(definition);
        var restored = JsonSerializer.Deserialize<AgentToolDefinition>(json);

        Assert.NotNull(restored);
        Assert.Equal(definition.Name, restored!.Name);
        Assert.Equal(card.Pack, restored.Pack);
        Assert.Equal(card.Purpose, restored.Purpose);
        Assert.Equal(card.WhenToUse, restored.WhenToUse);
        Assert.Equal(card.WhenNotToUse, restored.WhenNotToUse);
        Assert.Equal(card.Examples.ToArray(), restored.Examples.ToArray());
        Assert.Equal(card.Synonyms.ToArray(), restored.Synonyms.ToArray());
        Assert.Equal(card.NegativeExamples.ToArray(), restored.NegativeExamples.ToArray());
        Assert.Equal(card.Idempotent, restored.Idempotent);
        Assert.Equal(card.TimeoutMs, restored.TimeoutMs);
        Assert.Equal(card.RequiredContext, restored.RequiredContext);
        Assert.Equal(definition.SupportsBulk, restored.SupportsBulk);
        Assert.Equal(restored.SupportsBulk, restored.Bulk);
        Assert.Contains("\"Pack\":\"view_settings\"", json);
    }
}

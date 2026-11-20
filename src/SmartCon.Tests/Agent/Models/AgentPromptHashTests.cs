using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Models;

/// <summary>
/// H1-13 cache stability: the static prompt hash and the sorted tool-set hash
/// must be identical between turns; a project-context change (active view,
/// counters) must not touch the static hash, while a schema change must.
/// </summary>
public sealed class AgentPromptHashTests
{
    [Fact]
    public void ComputeStatic_IsStableLowercaseHex()
    {
        var prompt = AgentSystemPrompt.BuildStatic();
        var first = AgentPromptHash.ComputeStatic(prompt);
        var second = AgentPromptHash.ComputeStatic(prompt);

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void ComputeStatic_IsIndependentOfApprovalMode()
    {
        // P.3: mode is a turn-context delta, never part of the static prefix.
        var staticHash = AgentPromptHash.ComputeStatic(AgentSystemPrompt.BuildStatic());
        var context = Context("Project A", "План 1 этажа", 120);

        var confirmTurn = AgentSystemPrompt.BuildTurnContext(
            context, AgentApprovalMode.ConfirmWrites);
        var readOnlyTurn = AgentSystemPrompt.BuildTurnContext(
            context, AgentApprovalMode.ReadOnly);

        Assert.NotEqual(confirmTurn, readOnlyTurn);
        Assert.Contains("mode: ConfirmWrites", confirmTurn, StringComparison.Ordinal);
        Assert.Contains("## Approval mode: ReadOnly", readOnlyTurn, StringComparison.Ordinal);
        Assert.Equal(
            staticHash,
            AgentPromptHash.ComputeStatic(AgentSystemPrompt.BuildStatic()));
    }

    [Fact]
    public void ComputeToolSet_OrderIndependent()
    {
        var first = Tool("list_views", """{"type":"object","properties":{}}""");
        var second = Tool("rename_views", """{"type":"object","properties":{"items":{}}}""");

        Assert.Equal(
            AgentPromptHash.ComputeToolSet(new[] { first, second }),
            AgentPromptHash.ComputeToolSet(new[] { second, first }));
    }

    [Fact]
    public void ComputeToolSet_SchemaChange_ChangesHash()
    {
        var before = Tool("rename_views", """{"type":"object","properties":{}}""");
        var after = Tool("rename_views", """{"type":"object","properties":{"items":{}}}""");

        Assert.NotEqual(
            AgentPromptHash.ComputeToolSet(new[] { before }),
            AgentPromptHash.ComputeToolSet(new[] { after }));
    }

    [Fact]
    public void Compute_SameSetAcrossTurns_IsStable()
    {
        var prompt = AgentSystemPrompt.BuildStatic();
        var firstTurn = new[]
        {
            Tool("list_views", """{"type":"object","properties":{}}"""),
            Tool("rename_views", """{"type":"object","properties":{"items":{}}}""")
        };
        var nextTurn = new[]
        {
            Tool("rename_views", """{"type":"object","properties":{"items":{}}}"""),
            Tool("list_views", """{"type":"object","properties":{}}""")
        };

        Assert.Equal(
            AgentPromptHash.Compute(prompt, firstTurn),
            AgentPromptHash.Compute(prompt, nextTurn));
    }

    [Fact]
    public void Compute_ChangesWhenToolsChange()
    {
        var prompt = AgentSystemPrompt.BuildStatic();
        var one = Tool("list_views", """{"type":"object","properties":{}}""");
        var two = Tool("rename_views", """{"type":"object","properties":{}}""");

        Assert.NotEqual(
            AgentPromptHash.Compute(prompt, new[] { one }),
            AgentPromptHash.Compute(prompt, new[] { one, two }));
    }

    [Fact]
    public void BuildStatic_IgnoresProjectContext_WhileFullBuildChanges()
    {
        var contextA = Context("Project A", "План 1 этажа", 120);
        var contextB = Context("Project B", "Разрез А-А", 400);

        var fullA = AgentSystemPrompt.Build(contextA, AgentApprovalMode.ConfirmWrites);
        var fullB = AgentSystemPrompt.Build(contextB, AgentApprovalMode.ConfirmWrites);
        var staticPrompt = AgentSystemPrompt.BuildStatic();

        Assert.NotEqual(fullA, fullB);
        Assert.Contains("<session_context>", fullA);
        Assert.DoesNotContain("<session_context>", staticPrompt);
        Assert.DoesNotContain("<turn_context>", staticPrompt);
        Assert.DoesNotContain("План 1 этажа", staticPrompt);
        Assert.Contains("## Working rules for this session", staticPrompt);
        Assert.Equal(
            AgentPromptHash.ComputeStatic(staticPrompt),
            AgentPromptHash.ComputeStatic(AgentSystemPrompt.BuildStatic()));
    }

    [Fact]
    public void Build_AlwaysCarriesDiscoveryInstructions()
    {
        var context = Context("Project A", "План 1 этажа", 120);

        var prompt = AgentSystemPrompt.Build(context, AgentApprovalMode.ConfirmWrites);

        Assert.Contains("## Tool catalog", prompt);
        Assert.Contains("load_tool_pack", prompt);
        Assert.Contains("find_tools", prompt);
        Assert.Contains(
            "## Tool catalog",
            AgentSystemPrompt.BuildStatic());
    }

    private static AgentToolDefinition Tool(string name, string schema) =>
        new()
        {
            Name = name,
            Description = name,
            InputSchemaJson = schema
        };

    private static AgentProjectContext Context(string title, string viewName, int viewCount) =>
        new()
        {
            DocumentId = "doc-1",
            Title = title,
            RevitVersion = "2025",
            ActiveViewId = 1,
            ActiveViewName = viewName,
            ActiveViewType = "FloorPlan",
            LevelCount = 3,
            ViewCount = viewCount,
            SheetCount = 2,
            TopCategories = new[] { "Walls" }
        };
}

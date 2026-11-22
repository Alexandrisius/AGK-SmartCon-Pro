using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Parity gate between the integration-test production agent host and the
/// production catalog: the host composes the graph through the same shared
/// registration methods the application uses, so its tool registry, initial
/// tool set and system prompt must equal the production constants. No LLM
/// call: every test runs offline on a freshly created empty document.
/// </summary>
public sealed class ProductionAgentParityTests : RevitApiTest
{
    private Document? _document;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
    }

    [After(Test)]
    public void Close()
    {
        try
        {
            _document?.Close(false);
        }
        catch (Exception)
        {
            // The document may already be closed by a test scenario.
        }
    }

    [Test]
    public async Task Catalog_EqualsProductionCards()
    {
        using var host = ProductionAgentHost.Create(Doc);

        var registryNames = host.Registry.GetDefinitions()
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        var cardNames = AgentToolCards.All
            .Select(card => card.ToolName)
            .ToHashSet(StringComparer.Ordinal);

        // Known production plumbing that has no retrieval card: load_tool_pack
        // is the meta-tool that ships the cards, so it cannot describe itself.
        var expected = new HashSet<string>(cardNames, StringComparer.Ordinal)
        {
            AgentToolPacks.LoadToolPackName
        };

        var missingCards = string.Join(", ", registryNames.Except(expected).OrderBy(n => n, StringComparer.Ordinal));
        var missingTools = string.Join(", ", expected.Except(registryNames).OrderBy(n => n, StringComparer.Ordinal));

        await Assert.That(missingCards).IsEqualTo(string.Empty);
        await Assert.That(missingTools).IsEqualTo(string.Empty);
        await Assert.That(registryNames.Count).IsEqualTo(AgentToolCards.Count + 1);
    }

    [Test]
    public async Task AllTools_HaveValidDefinitions()
    {
        using var host = ProductionAgentHost.Create(Doc);

        var definitions = host.Registry.GetDefinitions();
        await Assert.That(definitions.Count).IsGreaterThan(0);

        foreach (var definition in definitions)
        {
            await Assert.That(string.IsNullOrWhiteSpace(definition.Name)).IsFalse();
            await Assert.That(string.IsNullOrWhiteSpace(definition.Description)).IsFalse();

            using var document = JsonDocument.Parse(definition.InputSchemaJson);
            await Assert.That(document.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);
        }
    }

    [Test]
    public async Task InitialToolSet_IsAlwaysAvailableCore()
    {
        using var host = ProductionAgentHost.Create(Doc);

        // Fresh session: nothing loaded and nothing pending, so the initial set
        // is exactly the always-available tools present in the registry (the
        // same rule AgentOrchestrator.SelectInitialToolNames applies).
        var session = new AgentSessionContext();
        await Assert.That(session.LoadedToolNames.Count).IsEqualTo(0);
        await Assert.That(session.PendingToolNames.Count).IsEqualTo(0);

        var initial = host.Registry.GetDefinitions()
            .Where(definition => AgentToolPacks.IsAlwaysAvailable(definition.Name))
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);

        var expectedCore = new[]
        {
            "get_project_overview", "list_categories", "list_levels", "list_views",
            "get_current_view_info", "get_view_settings", "query_elements",
            "summarize_elements", "get_element_details", "get_selected_elements",
            "find_tools"
        };
        var expectedSystem = new[]
        {
            AgentToolPacks.LoadToolPackName,
            AgentToolPacks.ReadToolResultName,
            "execute_plan"
        };

        var expected = new HashSet<string>(expectedCore.Length + expectedSystem.Length, StringComparer.Ordinal);
        foreach (var name in expectedCore.Concat(expectedSystem))
            expected.Add(name);

        var missing = string.Join(", ", expected.Except(initial).OrderBy(n => n, StringComparer.Ordinal));
        var extra = string.Join(", ", initial.Except(expected).OrderBy(n => n, StringComparer.Ordinal));

        await Assert.That(missing).IsEqualTo(string.Empty);
        await Assert.That(extra).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task SystemPrompt_IsProductionPrompt()
    {
        using var host = ProductionAgentHost.Create(Doc);

        var prompt = new AgentPromptBuilder().BuildStatic();
        await Assert.That(prompt).IsEqualTo(AgentSystemPrompt.BuildStatic());
        await Assert.That(prompt.Contains("## Tool catalog", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task FindTools_ResolvesInventoryQuery()
    {
        // Offline run: disable the decision layer the same way production does
        // when SMARTCON_DECIDER_ENABLED is not "1" — the router then falls back
        // to the local rule/BM25 router and never touches the network.
        var previous = Environment.GetEnvironmentVariable("SMARTCON_DECIDER_ENABLED");
        Environment.SetEnvironmentVariable("SMARTCON_DECIDER_ENABLED", "0");
        try
        {
            using var host = ProductionAgentHost.Create(Doc);

            var result = await host.Executor.ExecuteAsync(
                "find_tools",
                Parse(new { query = "какие семейства есть в модели" }),
                CancellationToken.None);

            await Assert.That(result.Success).IsTrue();
            var tools = result.Data!.Value.GetProperty("tools").EnumerateArray()
                .Select(card => card.GetProperty("name").GetString() ?? string.Empty)
                .ToList();
            await Assert.That(tools.Count).IsGreaterThan(0);
            await Assert.That(tools.Contains("list_families")).IsTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SMARTCON_DECIDER_ENABLED", previous);
        }
    }

    [Test]
    public async Task WriteTool_IsApprovedThroughProductionAutoApprove()
    {
        using var host = ProductionAgentHost.Create(Doc);

        // A write-path call with dryRun:false on the empty item set: the
        // executor must pass the production bypass (AutoApproveSession) instead
        // of rejecting the call for a missing approval.
        var result = await host.Executor.ExecuteAsync(
            "rename_views",
            Parse(new { items = Array.Empty<object>(), dryRun = false }),
            CancellationToken.None);

        var blocked = result.ErrorCode is not null
                      && (result.ErrorCode.StartsWith("confirmation", StringComparison.Ordinal)
                          || result.ErrorCode == "read_only_mode");
        await Assert.That(blocked).IsFalse();
    }

    private static JsonElement Parse(object args)
    {
        var json = JsonSerializer.Serialize(args);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

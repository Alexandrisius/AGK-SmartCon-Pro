using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
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
    public async Task ToolSchemas_KeepDocumentIdInsidePropertiesAndRootKeysClosed()
    {
        // Full production catalog guard for the schema-shape bug where
        // documentId sat at the schema ROOT next to properties: with
        // additionalProperties:false the validator reported it as unknown and
        // AgentToolInputRepair DELETED it, so background-model reads silently
        // ran against the active document. The unit-side twin
        // (AgentToolSchemaDocumentIdTests) sees only the agent-side tools -
        // RevitAPI is compile-time only there.
        using var host = ProductionAgentHost.Create(Doc);

        var allowedRootKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "type", "properties", "required", "additionalProperties", "$schema",
            "anyOf", "definitions"
        };
        var offenders = new List<string>();
        var documentIdTools = 0;

        foreach (var definition in host.Registry.GetDefinitions())
        {
            var schema = definition.InputSchemaJson;
            using var document = JsonDocument.Parse(schema);

            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (!allowedRootKeys.Contains(member.Name))
                {
                    offenders.Add(
                        definition.Name + ": unexpected root key '" + member.Name + "'");
                }
            }

            if (!schema.Contains("documentId", StringComparison.Ordinal))
                continue;

            var declaresProperty =
                document.RootElement.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty("documentId", out _);
            if (!declaresProperty)
            {
                offenders.Add(
                    definition.Name + ": schema mentions documentId but does not " +
                    "declare it in properties");
                continue;
            }

            documentIdTools++;
            using var arguments = JsonDocument.Parse("{\"documentId\":\"abc\"}");
            var repaired = AgentToolInputRepair.Repair(schema, arguments.RootElement);
            var kept = repaired.Arguments.ValueKind == JsonValueKind.Object
                       && repaired.Arguments.TryGetProperty("documentId", out var value)
                       && value.GetString() == "abc";
            if (!kept)
                offenders.Add(definition.Name + ": repair removed documentId");

            var errors = AgentToolSchemaValidator.Validate(schema, "{\"documentId\":\"abc\"}");
            if (errors.Any(error => error.Contains(
                    "$.documentId", StringComparison.Ordinal)))
            {
                offenders.Add(
                    definition.Name + ": validator flags documentId: " +
                    string.Join("; ", errors));
            }
        }

        await Assert.That(string.Join("; ", offenders)).IsEqualTo(string.Empty);
        await Assert.That(documentIdTools).IsGreaterThan(0);
    }

    [Test]
    public async Task InitialToolSet_FirstRequestCarriesAlwaysAvailableCore()
    {
        // One real production turn (scripted LLM, no tool calls): the first
        // request's tool list is what AgentOrchestrator.ResolveToolDefinitions
        // actually froze - not a recomputation of the rule on a throw-away
        // session.
        var fake = new FakeAgentLlmClient();
        fake.EnqueueAssistant("Готово.");
        using var host = ProductionAgentHost.Create(Doc, fake);
        var conversationId = host.CreateConversation("parity-initial-tools");

        await host.Orchestrator.RunTurnAsync(
            conversationId,
            "Что есть в модели?",
            Array.Empty<AgentAttachment>(),
            progress: null,
            CancellationToken.None);

        await Assert.That(fake.Requests.Count).IsGreaterThan(0);
        var initial = fake.Requests[0].Tools
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        await Assert.That(initial.Count).IsGreaterThan(0);

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
    public async Task SystemPrompt_FirstRequestUsesProductionStaticPrompt()
    {
        // One real production turn: the captured LLM request must open with the
        // production static prompt (built by the DI-registered prompt builder),
        // carry the per-turn <session_context> on top of the same prefix and
        // repeat the user text verbatim.
        var fake = new FakeAgentLlmClient();
        fake.EnqueueAssistant("Готово.");
        using var host = ProductionAgentHost.Create(Doc, fake);
        var conversationId = host.CreateConversation("parity-system-prompt");
        const string userText = "Проверка системного промпта.";

        await host.Orchestrator.RunTurnAsync(
            conversationId,
            userText,
            Array.Empty<AgentAttachment>(),
            progress: null,
            CancellationToken.None);

        await Assert.That(fake.Requests.Count).IsGreaterThan(0);
        var messages = fake.Requests[0].Messages;
        await Assert.That(messages.Count).IsGreaterThan(1);

        var staticPrompt = AgentSystemPrompt.BuildStatic();

        await Assert.That(messages[0].Role).IsEqualTo(AgentRole.System);
        await Assert.That((messages[0].Content ?? string.Empty)
            .StartsWith(staticPrompt, StringComparison.Ordinal)).IsTrue();

        var turnContext = messages.FirstOrDefault(message =>
            message.Role == AgentRole.System
            && (message.Content ?? string.Empty).Contains(
                AgentSystemPrompt.SessionContextHeader, StringComparison.Ordinal));
        // The turn context is a separate tail message by design (cacheable
        // static prefix + per-turn state), so it must merely exist.
        await Assert.That(turnContext).IsNotNull();

        var firstUser = messages.First(message => message.Role == AgentRole.User);
        await Assert.That(firstUser.Content).IsEqualTo(userText);
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

        // Arguments that pass schema validation (minItems:1) with a real write
        // intent (dryRun:false): the call must reach the confirmation branch,
        // take the production bypass (AutoApproveSession) and EXECUTE - the
        // view_not_found verdict about the fake id is the tool's own result,
        // not a gate rejection.
        var arguments = Parse(new
        {
            items = new[] { new { viewId = 123456, newName = "parity-renamed" } },
            dryRun = false
        });

        var result = await host.Executor.ExecuteAsync(
            "rename_views",
            arguments,
            CancellationToken.None);

        var blocked = result.ErrorCode is not null
                      && (result.ErrorCode.StartsWith("confirmation", StringComparison.Ordinal)
                          || result.ErrorCode == "read_only_mode");
        await Assert.That(blocked).IsFalse();

        // Positive signal: the production audit trail recorded the bypass.
        var audit = host.ReadAuditEntries();
        var bypassEntry = audit.FirstOrDefault(entry =>
            entry.Tool == "rename_views#auto_approved"
            && entry.Result == "auto_approved:bypass");
        await Assert.That(bypassEntry.Tool).IsEqualTo("rename_views#auto_approved");

        // Negative control: the SAME call through a ConfirmWrites session is
        // stopped by the confirmation gate (no UI subscriber in the headless
        // host -> fail-closed rejection), proving the first run really passed
        // a gate that is otherwise active.
        using var confirmingHost = ProductionAgentHost.Create(Doc);
        confirmingHost.Session.ApprovalMode = AgentApprovalMode.ConfirmWrites;
        var gateResult = await confirmingHost.Executor.ExecuteAsync(
            "rename_views",
            arguments,
            CancellationToken.None);

        await Assert.That(gateResult.Success).IsFalse();
        await Assert.That(gateResult.ErrorCode).IsEqualTo("confirmation_rejected");
    }

    private static JsonElement Parse(object args)
    {
        var json = JsonSerializer.Serialize(args);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

using System.IO;
using System.Text;
using System.Text.Json;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Decisions;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Live;

/// <summary>
/// Live end-to-end check of agent-driven tool discovery: the first shortlist
/// contains only read tools + find_tools, so a real LLM must explore the model,
/// search the catalog with a concrete query/context and then propose a plan.
/// No Revit: tools are scripted, the orchestration/LLM/routing path is real.
/// Skipped unless SMARTCON_AGENT_LIVE=1 and an API key is configured.
/// </summary>
public sealed class AbstractTaskLiveTests
{
    [Fact]
    public async Task AbstractTask_AgentExploresAndFindsToolsItself()
    {
        if (Environment.GetEnvironmentVariable("SMARTCON_AGENT_LIVE") != "1")
            return;

        var settings = AgentLlmSettings.Load();
        Assert.True(settings.HasApiKey, "Set an OpenRouter key before a live run.");

        var readViews = new FakeTool(
            "list_views",
            "List project views with id, name, type and template.",
            AgentToolRisk.Read,
            """
            {"views":[{"id":1,"name":"SC_Level_1_План","type":"План этажа"},
                      {"id":2,"name":"SC_Level_2_План","type":"План этажа"},
                      {"id":3,"name":"SC_Ref","type":"План этажа"},
                      {"id":4,"name":"SC_Разрез","type":"Разрез"}],"total":4}
            """);
        var listLevels = new FakeTool(
            "list_levels",
            "List levels with id, name and elevation.",
            AgentToolRisk.Read,
            """
            {"levels":[{"id":10,"name":"Уровень 1","elevationMm":0},
                       {"id":11,"name":"Уровень 2","elevationMm":3000}],"total":2}
            """);
        var viewSettings = new FakeTool(
            "get_view_settings",
            "Read current settings of a view: template, scale, view range, crop, hidden categories.",
            AgentToolRisk.Read,
            """
            {"viewId":3,"name":"SC_Ref","scale":100,"hasTemplate":false,
             "crop":{"active":true,"visible":true,"minMm":[-5000,-5000],"maxMm":[5000,5000]},
             "hiddenCategories":[{"name":"Мебель","hidden":true}]}
            """);
        var renameViews = new FakeTool(
            "rename_views",
            "Rename existing views by id.",
            AgentToolRisk.Write,
            """
            {"dryRun":true,"changed":[],"skipped":[],"failed":[],"total":0,"affected":0}
            """);
        var setCrop = new FakeTool(
            "set_view_crop",
            "Enable, disable or resize the crop region of a view.",
            AgentToolRisk.Write,
            """
            {"dryRun":true,"changed":[],"skipped":[],"failed":[],"total":0,"affected":0}
            """);
        var setVisibility = new FakeTool(
            "set_view_visibility",
            "Hide or show categories in a view.",
            AgentToolRisk.Write,
            """
            {"dryRun":true,"changed":[],"skipped":[],"failed":[],"total":0,"affected":0}
            """);

        var registry = new AgentToolRegistry(new IAgentTool[]
        {
            readViews, listLevels, viewSettings, renameViews, setCrop, setVisibility
        });

        var decisions = AgentDecisionsSettings.Load();
        using var decisionClient = new HttpDecisionClient(decisions);
        IToolRouter searchRouter = decisions.IsAvailable
            ? new DecisionsToolRouter(new RuleToolRouter(), decisionClient)
            : new RuleToolRouter();
        var findTools = new FindToolsTool(searchRouter);
        findTools.Attach(registry);
        registry = new AgentToolRegistry(
            new IAgentTool[]
            {
                readViews, listLevels, viewSettings, renameViews, setCrop, setVisibility,
                findTools
            });

        var initialRouter = new FixedShortlistRouter(new[] { "list_views", "list_levels",
            "get_view_settings", "find_tools" });
        var toolCalls = new CapturingToolCallStore();
        var session = new AgentSessionContext
        {
            DocumentId = "live-abstract",
            ApprovalMode = AgentApprovalMode.ReadOnly
        };
        var executor = new AgentToolExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            session,
            new AutoApproveConfirmation());
        var orchestrator = new AgentOrchestrator(
            new OpenAiCompatibleLlmClient(settings),
            executor,
            registry,
            new InMemoryConversationStore(),
            new NullAuditLog(),
            new StubProjectContextProvider(),
            settings,
            session,
            new AgentPromptBuilder(),
            toolCalls,
            null,
            initialRouter);

        var task =
            "В проекте есть эталонный вид SC_Ref и несколько планов этажей. Наведи порядок: " +
            "имена планов приведи к шаблону SC_Level_<номер>, подрезку и видимость сделай " +
            "как у SC_Ref, лишние категории скрой. Сначала изучи модель и предложи пошаговый " +
            "план. Ничего не меняй.";

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            task,
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var transcript = new StringBuilder();
        transcript.AppendLine("# Abstract-task live run");
        transcript.AppendLine();
        transcript.AppendLine("- Model: " + settings.Model);
        transcript.AppendLine("- Provider: " + settings.Provider);
        transcript.AppendLine("- Decision router: " + (decisions.IsAvailable
            ? decisions.Provider + "/" + decisions.Model
            : "rule fallback"));
        transcript.AppendLine("- User: " + task);
        transcript.AppendLine("- Rounds: " + result.Rounds);
        transcript.AppendLine("- Tool calls: " + result.ToolCallCount);
        transcript.AppendLine();
        transcript.AppendLine("## Calls");
        foreach (var call in toolCalls.Records)
        {
            transcript.AppendLine("- `" + call.ToolName + "` " +
                                  (call.ArgumentsJson ?? string.Empty));
            transcript.AppendLine("  → " + (call.Status ?? "?") + " " +
                                  (call.Error ?? string.Empty));
        }

        transcript.AppendLine();
        transcript.AppendLine("## Answer");
        transcript.AppendLine();
        transcript.AppendLine(result.FinalText);

        var directory = Path.Combine(Path.GetTempPath(), "smartcon-agent-abstract-live");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
        await File.WriteAllTextAsync(path, transcript.ToString(), Encoding.UTF8);

        var calledNames = toolCalls.Records.Select(record => record.ToolName).ToList();
        Assert.True(
            calledNames.Contains("find_tools"),
            "The agent did not search the catalog. Calls: " + string.Join(", ", calledNames) +
            ". Transcript: " + path);
        Assert.Contains(calledNames, name =>
            name is "list_views" or "list_levels" or "get_view_settings");
        Assert.False(string.IsNullOrWhiteSpace(result.FinalText));

        var search = toolCalls.Records.First(record => record.ToolName == "find_tools");
        Assert.Contains("query", search.ArgumentsJson ?? string.Empty);
    }

    private sealed class FakeTool : IAgentTool
    {
        private readonly string _payload;

        public FakeTool(string name, string description, AgentToolRisk risk, string payload)
        {
            _payload = payload;
            Definition = new AgentToolDefinition
            {
                Name = name,
                Description = description,
                Risk = risk,
                SupportsDryRun = risk != AgentToolRisk.Read,
                InputSchemaJson = @"{""type"":""object"",""properties"":{}}"
            };
        }

        public AgentToolDefinition Definition { get; }

        public Task<ToolResult> ExecuteAsync(
            AgentToolContext context,
            JsonElement args,
            CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok(
                JsonDocument.Parse(_payload).RootElement.Clone(), "ok"));
    }

    private sealed class FixedShortlistRouter : IToolRouter
    {
        private readonly HashSet<string> _names;

        public FixedShortlistRouter(IEnumerable<string> names)
        {
            _names = new HashSet<string>(names, StringComparer.Ordinal);
        }

        public Task<ToolRoutingDecision> RouteAsync(
            string query,
            IReadOnlyList<AgentToolDefinition> tools,
            int maxTools = 14,
            CancellationToken ct = default) =>
            Task.FromResult(new ToolRoutingDecision
            {
                Tools = tools.Where(tool => _names.Contains(tool.Name)).ToList()
            });
    }

    private sealed class CapturingToolCallStore : IAgentToolCallStore
    {
        public List<AgentToolCallRecord> Records { get; } = new();

        public Task RecordAsync(AgentToolCallRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class AutoApproveConfirmation : IAgentConfirmationService
    {
        public Task<AgentConfirmationResult> RequestAsync(
            AgentConfirmationRequest request,
            CancellationToken ct) =>
            Task.FromResult(AgentConfirmationResult.Approved);
    }

    private sealed class ImmediateAwaitableEvent : IAgentAwaitableEvent
    {
        public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(funcWithApp(new object()));
        }

        public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            actionWithApp(new object());
            return Task.CompletedTask;
        }

        public void ProcessQueue(object revitApp)
        {
        }

        public void Initialize(Action onRaise)
        {
        }
    }

    private sealed class NullAuditLog : IAgentAuditLog
    {
        public Task WriteAsync(AgentAuditEntry entry, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class StubProjectContextProvider : IAgentProjectContextProvider
    {
        public Task<AgentProjectContext> GetAsync(CancellationToken ct) =>
            Task.FromResult(new AgentProjectContext
            {
                DocumentId = "live-abstract",
                Title = "Abstract live test",
                RevitVersion = "2025"
            });
    }

    private sealed class InMemoryConversationStore : IAgentConversationStore
    {
        public List<AgentMessage> Messages { get; } = new();

        public Task<Guid> CreateConversationAsync(
            AgentConversationHeader header,
            CancellationToken ct) => Task.FromResult(Guid.NewGuid());

        public Task<IReadOnlyList<AgentConversationHeader>> ListConversationsAsync(
            string documentId,
            int limit,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentConversationHeader>>(
                Array.Empty<AgentConversationHeader>());

        public Task<AgentConversationHeader?> GetConversationAsync(
            Guid conversationId,
            CancellationToken ct) =>
            Task.FromResult<AgentConversationHeader?>(null);

        public Task AppendMessageAsync(
            Guid conversationId,
            AgentMessage message,
            CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(
            Guid conversationId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentMessage>>(Messages.ToList());

        public Task UpdateTitleAsync(
            Guid conversationId,
            string title,
            CancellationToken ct) => Task.CompletedTask;

        public Task DeleteConversationAsync(Guid conversationId, CancellationToken ct) =>
            Task.CompletedTask;
    }
}

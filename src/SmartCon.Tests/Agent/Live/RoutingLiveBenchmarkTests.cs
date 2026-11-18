using System.IO;
using System.Runtime.CompilerServices;
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
using Xunit.Abstractions;

namespace SmartCon.Tests.Agent.Live;

/// <summary>
/// Live routing benchmark for the phase-3 experiment: the production
/// orchestrator (shortlist + find_tools + load_tool_pack) runs a set of real
/// Russian intents against the full 33-tool catalog with scripted tools. It
/// measures rounds to the first correct call and how often the model has to
/// search the catalog, in two modes: rule router vs decision router inside
/// find_tools.
/// Skipped unless SMARTCON_AGENT_LIVE=1 and an API key are configured.
/// </summary>
public sealed class RoutingLiveBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public RoutingLiveBenchmarkTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Plausible tool payloads: without ids in tool results a write task cannot
    /// be completed and the benchmark would measure the fake instead of routing.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Payloads =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["get_project_overview"] =
                @"{""title"":""SC benchmark"",""levelCount"":2,""viewCount"":5,""categoryCount"":10}",
            ["list_views"] =
                @"{""total"":2,""views"":[{""viewId"":101,""name"":""План 1"",""viewType"":""FloorPlan""}," +
                @"{""viewId"":102,""name"":""Разрез 1"",""viewType"":""Section""}]}",
            ["get_current_view_info"] =
                @"{""viewId"":101,""name"":""План 1"",""viewType"":""FloorPlan"",""scale"":100}",
            ["list_levels"] =
                @"{""total"":2,""levels"":[{""levelId"":11,""name"":""Этаж 1"",""elevationMm"":0}," +
                @"{""levelId"":12,""name"":""Этаж 2"",""elevationMm"":3000}]}",
            ["list_sheets"] =
                @"{""total"":1,""sheets"":[{""sheetId"":501,""number"":""АР-101"",""name"":""План 1 этажа"",""viewCount"":1}]}",
            ["get_sheet_content"] =
                @"{""sheet"":{""sheetId"":501,""number"":""АР-101"",""name"":""План 1 этажа""}," +
                @"""placedViewCount"":1,""views"":[{""viewId"":101,""name"":""План 1"",""centerXMm"":100,""centerYMm"":100}],""schedules"":[]}",
            ["list_schedules"] =
                @"{""total"":1,""schedules"":[{""scheduleId"":701,""name"":""Отделка"",""category"":""OST_Rooms"",""rowCount"":10}]}",
            ["get_schedule_data"] =
                @"{""scheduleId"":701,""name"":""Отделка"",""columns"":[{""index"":0,""heading"":""Помещение""}],""totalRows"":1,""rows"":[[""101""]]}",
            ["query_elements"] =
                @"{""total"":5,""elements"":[" +
                @"{""elementId"":201,""category"":""OST_Walls"",""typeName"":""Стена 200"",""levelName"":""Этаж 1""}," +
                @"{""elementId"":202,""category"":""OST_Walls"",""typeName"":""Стена 200"",""levelName"":""Этаж 1""}," +
                @"{""elementId"":203,""category"":""OST_PipeCurves"",""typeName"":""Труба Т1"",""levelName"":""Этаж 1""}," +
                @"{""elementId"":204,""category"":""OST_PipeCurves"",""typeName"":""Труба Т1"",""levelName"":""Этаж 2""}," +
                @"{""elementId"":205,""category"":""OST_Doors"",""typeName"":""Дверь 1"",""levelName"":""Этаж 1""}]}",
            ["get_element_details"] =
                @"{""elements"":[{""elementId"":201,""category"":""OST_Walls"",""parameters"":{""Марка"":""АР-1""}}]}",
            ["summarize_elements"] =
                @"{""total"":5,""groups"":[{""key"":""OST_Walls"",""count"":3}," +
                @"{""key"":""OST_PipeCurves"",""count"":2}," +
                @"{""key"":""OST_Doors"",""count"":1}]}",
            ["list_categories"] =
                @"{""total"":3,""categories"":[{""name"":""OST_Walls"",""elementCount"":3}," +
                @"{""name"":""OST_PipeCurves"",""elementCount"":2}," +
                @"{""name"":""OST_Doors"",""elementCount"":1}]}",
            ["create_view_filter"] =
                @"{""dryRun"":true,""filterId"":900,""created"":true,""reused"":false,""changed"":[{""id"":101}],""skipped"":[],""failed"":[],""total"":1,""affected"":1,""sections"":[{""section"":""views"",""status"":""ok""}]}",
            ["set_filter_overrides"] =
                @"{""dryRun"":true,""filterId"":900,""changed"":[{""id"":101}],""skipped"":[],""failed"":[],""total"":1,""affected"":1,""readBack"":[{""viewId"":101}],""sections"":[{""section"":""views"",""status"":""ok""}]}",
            ["read_tool_result"] = @"{""content"":""ok""}",
            ["execute_plan"] = @"{""planId"":""p1"",""steps"":[]}"
        };

    private static readonly (string Prompt, string[] Expected)[] Tasks =
    {
        ("Покажи все листы проекта", new[] { "list_sheets" }),
        ("Что размещено на листе АР-101?", new[] { "get_sheet_content" }),
        ("Какие ведомости есть в проекте?", new[] { "list_schedules" }),
        ("Выведи первые строки ведомости отделки", new[] { "get_schedule_data" }),
        ("Убери с вида 101 трубы системы Т1", new[] { "create_view_filter" }),
        ("Покрась стены в красный на этом виде", new[] { "set_filter_overrides" }),
        ("Сколько видов в проекте?", new[] { "list_views", "get_project_overview" }),
        ("Найди все двери на первом этаже", new[] { "query_elements" }),
        ("Переименуй вид 101 в «План 1 этажа»", new[] { "rename_views" }),
        ("Скрой категорию осей на виде", new[] { "set_view_visibility" })
    };

    /// <summary>
    /// Paraphrases without the catalog keywords: the expected tool is often
    /// outside the BM25 top-5, so the agent has to search the catalog.
    /// </summary>
    private static readonly (string Prompt, string[] Expected)[] HardTasks =
    {
        ("Сделай так, чтобы трубы Т1 не мешали на виде 101", new[] { "create_view_filter" }),
        ("Хочу видеть стены залитыми красным на виде 101", new[] { "set_filter_overrides" }),
        ("Покажи, что на чертеже АР-101", new[] { "get_sheet_content" }),
        ("Дай таблицу отделки по помещениям", new[] { "get_schedule_data" }),
        ("Сколько всего чертежей в альбоме?", new[] { "list_sheets" }),
        ("Настрой графику так, чтобы стены выглядели бледно на виде 101",
            new[] { "set_filter_overrides", "create_view_filter" })
    };

    [Fact]
    public async Task LiveBenchmark_RoundsAndCatalogSearch()
    {
        if (Environment.GetEnvironmentVariable("SMARTCON_AGENT_LIVE") != "1")
            return;

        var settings = AgentLlmSettings.Load();
        Assert.True(settings.HasApiKey, "Set an OpenRouter key before a live run.");

        var decisions = AgentDecisionsSettings.Load();
        var modes = new List<string> { "rules" };
        HttpDecisionClient? decisionClient = null;
        if (decisions.IsAvailable)
        {
            decisionClient = new HttpDecisionClient(decisions);
            modes.Add("decisions");
        }

        var transcript = new StringBuilder();
        transcript.AppendLine("# Live routing benchmark");
        transcript.AppendLine();
        transcript.AppendLine("- Model: " + settings.Model);
        transcript.AppendLine("- Provider: " + settings.Provider);
        transcript.AppendLine("- Decision router: " + (decisions.IsAvailable
            ? decisions.Provider + "/" + decisions.Model
            : "disabled (rules only)"));
        transcript.AppendLine();

        try
        {
            foreach (var mode in modes)
            {
                var lines = await RunModeAsync(mode, settings, decisionClient);
                foreach (var line in lines)
                {
                    _output.WriteLine(line);
                    transcript.AppendLine(line);
                }

                transcript.AppendLine();
            }
        }
        finally
        {
            decisionClient?.Dispose();
        }

        var directory = Path.Combine(Path.GetTempPath(), "smartcon-agent-routing-live");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
        await File.WriteAllTextAsync(path, transcript.ToString(), Encoding.UTF8);
        _output.WriteLine("transcript: " + path);
    }

    private static async Task<IReadOnlyList<string>> RunModeAsync(
        string mode,
        AgentLlmSettings settings,
        HttpDecisionClient? decisionClient)
    {
        var results = new List<(string Prompt, bool Success, bool Visible, int FirstRound,
            int Rounds, int FindTools, int LoadPacks, List<string> Calls, string Answer)>();

        foreach (var (prompt, expected) in Tasks.Concat(HardTasks))
        {
            var counter = new RoundCounter();
            var log = new CallLog(counter);
            var session = new AgentSessionContext
            {
                DocumentId = "live-benchmark",
                ApprovalMode = AgentApprovalMode.AutoApproveSession
            };

            var fakeTools = AgentToolCards.All
                .Where(card => card.ToolName != "find_tools")
                .Select(card => (IAgentTool)new FakeTool(
                    card.ToolName,
                    card.Purpose,
                    log,
                    Payloads.TryGetValue(card.ToolName, out var payload)
                        ? payload
                        : @"{""ok"":true}"))
                .ToList();

            var loadPack = new LoadToolPackTool(session);
            IToolRouter router = mode == "decisions" && decisionClient is not null
                ? new DecisionsToolRouter(new RuleToolRouter(), decisionClient)
                : new RuleToolRouter();
            var findTools = new FindToolsTool(router);
            var registry = new AgentToolRegistry(
                fakeTools.Concat(new IAgentTool[] { loadPack, findTools }),
                findTools,
                loadPack);

            var executor = new AgentToolExecutor(
                registry,
                new ImmediateAwaitableEvent(),
                session,
                new AutoApproveConfirmation());
            var llm = new RoundCountingLlmClient(new OpenAiCompatibleLlmClient(settings), counter);
            var orchestrator = new AgentOrchestrator(
                llm,
                executor,
                registry,
                new InMemoryConversationStore(),
                new NullAuditLog(),
                new StubProjectContextProvider(),
                settings,
                session,
                new AgentPromptBuilder(),
                new CapturingToolCallStore());

            var result = await orchestrator.RunTurnAsync(
                Guid.NewGuid(),
                prompt,
                Array.Empty<AgentAttachment>(),
                null,
                CancellationToken.None);

            var calls = log.Calls.Select(call => call.Name).ToList();
            var firstCorrect = log.Calls
                .Where(call => expected.Contains(call.Name, StringComparer.Ordinal))
                .Select(call => call.Round)
                .DefaultIfEmpty(0)
                .Min();
            var visible = counter.ToolSets.Count > 0
                          && counter.ToolSets[0].Any(name =>
                              expected.Contains(name, StringComparer.Ordinal));

            results.Add((
                prompt,
                firstCorrect > 0,
                visible,
                firstCorrect,
                result.Rounds,
                calls.Count(name => name == "find_tools"),
                calls.Count(name => name == "load_tool_pack"),
                calls,
                result.FinalText));
        }

        var success = results.Count(item => item.Success);
        var found = results.Where(item => item.Success).ToList();
        var firstRounds = found.Select(item => item.FirstRound).OrderBy(value => value).ToList();
        var lines = new List<string>
        {
            "mode=" + mode +
            " tasks=" + results.Count +
            " success=" + success + "/" + results.Count +
            " visibleAtStart=" + results.Count(item => item.Visible) + "/" + results.Count +
            " avgFirstRound=" + (found.Count == 0 ? "n/a" : firstRounds.Average().ToString("F2")) +
            " p95FirstRound=" + (found.Count == 0 ? "n/a" : Percentile(firstRounds, 0.95).ToString("F1")) +
            " avgRounds=" + results.Average(item => item.Rounds).ToString("F2") +
            " findTools=" + results.Sum(item => item.FindTools) +
            " loadPacks=" + results.Sum(item => item.LoadPacks)
        };

        foreach (var item in results.Where(item => !item.Success))
        {
            var answer = item.Answer ?? string.Empty;
            if (answer.Length > 240)
                answer = answer.Substring(0, 240);
lines.Add("  miss: " + item.Prompt + " calls=" + string.Join(",", item.Calls));
            lines.Add("    answer: " + answer
                .Replace('\r', ' ')
                .Replace('\n', ' '));
        }

        return lines;
    }

    private static double Percentile(IReadOnlyList<int> values, double percentile)
    {
        if (values.Count == 0)
            return 0;

        var index = (int)Math.Ceiling(percentile * values.Count) - 1;
        return values[Math.Max(0, Math.Min(values.Count - 1, index))];
    }

    private sealed class RoundCounter
    {
        public int Requests { get; set; }

        public List<List<string>> ToolSets { get; } = new();
    }

    private sealed class CallLog
    {
        public CallLog(RoundCounter counter) => Counter = counter;

        public RoundCounter Counter { get; }

        public List<(string Name, int Round)> Calls { get; } = new();
    }

    private sealed class FakeTool : IAgentTool
    {
        private readonly CallLog _log;
        private readonly string _payload;

        public FakeTool(string name, string description, CallLog log, string payload)
        {
            _log = log;
            _payload = payload;
            Definition = new AgentToolDefinition
            {
                Name = name,
                Description = description,
                Risk = AgentToolRisk.Read,
                SupportsDryRun = false,
                InputSchemaJson =
                    @"{""type"":""object"",""properties"":{},""additionalProperties"":true}"
            };
        }

        public AgentToolDefinition Definition { get; }

        public Task<ToolResult> ExecuteAsync(
            AgentToolContext context,
            JsonElement args,
            CancellationToken ct)
        {
            _log.Calls.Add((Definition.Name, _log.Counter.Requests));
            using var document = JsonDocument.Parse(_payload);
            return Task.FromResult(ToolResult.Ok(
                document.RootElement.Clone(),
                "ok"));
        }
    }

    private sealed class RoundCountingLlmClient : IAgentLlmClient
    {
        private readonly IAgentLlmClient _inner;
        private readonly RoundCounter _counter;

        public RoundCountingLlmClient(IAgentLlmClient inner, RoundCounter counter)
        {
            _inner = inner;
            _counter = counter;
        }

        public string ProviderName => _inner.ProviderName;

        public string Model => _inner.Model;

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            AgentChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            _counter.Requests++;
            _counter.ToolSets.Add(request.Tools
                .Select(tool => tool.Name)
                .ToList());
            await foreach (var item in _inner.StreamAsync(request, ct).WithCancellation(ct))
                yield return item;
        }
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
                DocumentId = "live-benchmark",
                Title = "Routing benchmark",
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
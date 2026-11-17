using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Live dogfooding: a real LLM drives the read tools on a seeded model and the
/// transcript (tasks, calls, errors, answers, feedback) is written to
/// %TEMP%\smartcon-agent-dogfood. Read-only by design.
/// Skipped unless SMARTCON_AGENT_LIVE=1 and an API key is configured.
/// </summary>
public sealed class AgentLiveDogfoodTests : RevitApiTest
{
    private const int MaxRequests = 30;
    private const int MaxRoundsPerTask = 4;

    private const string SeededLevelName = "SC_Dogfood_Level";
    private const string SeededPlanName = "SC_Dogfood_Plan";
    private const string SeededComment = "DogfoodSeed";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private long _planId;
    private long _wallId;
    private bool _wallCreated;

    private Document Doc => _document!;

    [Test]
    public async Task LiveDogfood_ReadOnlyTooling()
    {
        var config = DogfoodConfig.TryLoad();
        if (config is null)
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        Seed();
        var feedback = new InMemoryFeedbackStore();
        var tools = CreateTools();
        var feedbackTool = (IAgentTool)new DogfoodFeedbackTool(feedback, config);
        var definitions = tools
            .Select(tool => tool.Definition)
            .Concat(new[] { feedbackTool.Definition })
            .ToList();

        var transcript = new StringBuilder();
        transcript.AppendLine("# SmartCon Agent — live dogfood transcript");
        transcript.AppendLine();
        transcript.AppendLine("- Provider: " + config.Provider);
        transcript.AppendLine("- Model: " + config.Model);
        transcript.AppendLine("- Revit: " + Doc.Application.VersionNumber);
        transcript.AppendLine("- Started: " + DateTime.Now.ToString("O"));
        transcript.AppendLine();

        var tasks = BuildTasks();
        var answered = 0;
        var requests = 0;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.ApiKey);

        var messages = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "system", ["content"] = SystemPrompt() }
        };

        try
        {
            for (var index = 0; index < tasks.Count; index++)
            {
                if (requests >= MaxRequests)
                {
                    transcript.AppendLine(
                        "## Task " + (index + 1) + " — skipped (request budget " + MaxRequests + " reached)");
                    transcript.AppendLine();
                    continue;
                }

                transcript.AppendLine("## Task " + (index + 1));
                transcript.AppendLine();
                transcript.AppendLine("**User:** " + tasks[index]);
                transcript.AppendLine();

                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = tasks[index]
                });

                string? finalAnswer = null;
                for (var round = 0; round < MaxRoundsPerTask && requests < MaxRequests; round++)
                {
                    requests++;
                    var response = await DogfoodClient.CompleteAsync(
                            http, config, messages, definitions, CancellationToken.None)
                        .ConfigureAwait(false);

                    if (response.Error is not null)
                    {
                        transcript.AppendLine("**LLM error:** " + response.Error);
                        transcript.AppendLine();
                        break;
                    }

                    if (response.ToolCalls.Count == 0)
                    {
                        finalAnswer = response.Content ?? "(empty answer)";
                        messages.Add(new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = finalAnswer
                        });
                        break;
                    }

                    transcript.AppendLine(
                        "**Assistant tool calls (" + response.ToolCalls.Count + "):**");
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = response.Content,
                        ["tool_calls"] = response.ToolCalls.Select(call => new Dictionary<string, object?>
                        {
                            ["id"] = call.Id,
                            ["type"] = "function",
                            ["function"] = new Dictionary<string, object?>
                            {
                                ["name"] = call.Name,
                                ["arguments"] = call.ArgumentsJson
                            }
                        }).ToList()
                    });

                    foreach (var call in response.ToolCalls)
                    {
                        var resultText = ExecuteTool(call, tools, feedbackTool);
                        transcript.AppendLine(
                            "- `" + call.Name + "` " + Shorten(call.ArgumentsJson, 300));
                        transcript.AppendLine(
                            "  → " + Shorten(resultText, 600));

                        messages.Add(new Dictionary<string, object?>
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = call.Id,
                            ["content"] = resultText
                        });
                    }

                    transcript.AppendLine();
                }

                if (finalAnswer is not null)
                {
                    answered++;
                    transcript.AppendLine("**Answer:** " + finalAnswer);
                }
                else
                {
                    transcript.AppendLine("**Answer:** (no final answer within the round budget)");
                }

                transcript.AppendLine();
            }
        }
        finally
        {
            transcript.AppendLine();
            transcript.AppendLine("## Tooling feedback reported by the model");
            transcript.AppendLine();
            if (feedback.Entries.Count == 0)
            {
                transcript.AppendLine("(none)");
            }
            else
            {
                foreach (var entry in feedback.Entries)
                {
                    transcript.AppendLine(
                        "- [" + entry.Area + " · " + entry.Rating + "/5 · " +
                        (entry.ToolName ?? "-") + "] " + entry.Issue +
                        (string.IsNullOrEmpty(entry.Suggestion) ? string.Empty
                            : " → " + entry.Suggestion));
                }
            }

            transcript.AppendLine();
            transcript.AppendLine("## Summary");
            transcript.AppendLine();
            transcript.AppendLine("- Tasks: " + tasks.Count);
            transcript.AppendLine("- Answered: " + answered);
            transcript.AppendLine("- LLM requests: " + requests + " / " + MaxRequests);
            transcript.AppendLine("- Feedback entries: " + feedback.Entries.Count);

            var directory = Path.Combine(Path.GetTempPath(), "smartcon-agent-dogfood");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
            File.WriteAllText(path, transcript.ToString(), Encoding.UTF8);

            SmartConLogger.Info("Agent live dogfood transcript: " + path);
            Console.WriteLine("Dogfood transcript: " + path);

            await Assert.That(File.Exists(path)).IsTrue();
            await Assert.That(answered).IsGreaterThanOrEqualTo(Math.Max(1, tasks.Count - 2));
        }
    }

    [After(Test)]
    public void Close()
    {
        _document?.Close(false);
    }

    private void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _transactions.RunInTransaction(Doc, "Seed dogfood model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            level.Name = SeededLevelName;
            var plan = ModelSeed.CreateViewPlanChecked(doc, level.Id, SeededPlanName);
            _planId = plan.Id.GetValue();

            var wall = ModelSeed.TryCreateWall(doc, level.Id);
            _wallCreated = wall is not null;
            _wallId = wall?.Id.GetValue() ?? 0;
            wall?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(SeededComment);
        });
    }

    private List<AgentReadToolBase> CreateTools()
    {
        var context = new StubRevitContext(Doc);
        return new List<AgentReadToolBase>
        {
            new GetProjectOverviewTool(context),
            new ListViewsTool(context),
            new ListLevelsTool(context),
            new ListCategoriesTool(context),
            new QueryElementsTool(context),
            new GetElementDetailsTool(context),
            new SummarizeElementsTool(context),
            new ListViewTemplatesTool(context),
            new GetViewSettingsTool(context),
            new GetViewTemplateDetailsTool(context),
            new GetCurrentViewInfoTool(context)
        };
    }

    private List<string> BuildTasks()
    {
        var tasks = new List<string>
        {
            "Что за проект? Кратко: сколько уровней, видов и категорий.",
            "Перечисли виды планов этажей и их уровни, не больше 10.",
            "Какие шаблоны видов есть в проекте?",
            "Покажи настройки вида '" + SeededPlanName + "': шаблон, область видимости, view range.",
            "Сколько стен в модели и как они распределены по уровням?",
            "Найди все стены с комментарием, содержащим " + SeededComment + ".",
            "Сколько элементов в каждой категории? Дай топ-10 категорий.",
            "Сводка по стенам: суммарная длина по уровням."
        };

        if (_wallCreated)
        {
            tasks.Add("Дай детали элемента " + _wallId +
                      ", только непустые параметры, без параметров типа.");
        }
        else
        {
            tasks.Add("Есть ли виды без шаблона? Сколько и примеры?");
        }

        tasks.Add("Что сейчас открыто (активный вид) и какой у него масштаб?");
        tasks.Add("Предложи план: как переименовать все планы этажей по образцу '" +
                  SeededPlanName + "' и применить к ним его настройки. Ничего не меняй, только шаги.");

        return tasks;
    }

    private string ExecuteTool(
        DogfoodToolCall call,
        IReadOnlyList<AgentReadToolBase> tools,
        IAgentTool feedbackTool)
    {
        JsonElement args;
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            args = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return "{\"success\":false,\"errorCode\":\"invalid_arguments\",\"message\":\"" +
                   ex.Message.Replace("\"", "'") + "\"}";
        }

        if (string.Equals(call.Name, "report_tooling_feedback", StringComparison.Ordinal))
        {
            var context = new AgentToolContext(
                new ImmediateAwaitableEvent(),
                "dogfood",
                Guid.Empty);
            var result = feedbackTool
                .ExecuteAsync(context, args, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return JsonSerializer.Serialize(result);
        }

        var tool = tools.FirstOrDefault(candidate =>
            string.Equals(candidate.Definition.Name, call.Name, StringComparison.Ordinal));
        if (tool is null)
        {
            return "{\"success\":false,\"errorCode\":\"unknown_tool\",\"message\":\"Unknown tool " +
                   call.Name + "\"}";
        }

        var toolResult = tool.ExecuteForTests(Doc, args, CancellationToken.None);
        return ToolResultBudget.Serialize(toolResult, 8000);
    }

    private static string SystemPrompt() =>
        "You are SmartCon Agent inside Autodesk Revit (live dogfood session, read-only). " +
        "Answer in Russian. Use the tools to read the model; never invent ids. " +
        "Prefer summarize_elements and list_views with summaryOnly for counts, " +
        "query_elements with filters and fields, get_element_details with parameterNames/onlyNonEmpty. " +
        "If a tool result has truncated=true, use offset/nextOffset or narrow the filters; " +
        "never repeat an identical call. Keep answers short: a summary plus a compact table. " +
        "If a tool is inconvenient or lacks data, call report_tooling_feedback once per distinct " +
        "problem with a concrete suggestion. This session is READ-ONLY: do not propose executing writes.";

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max) + "…";

    private sealed class StubRuntimeInfo : IAgentRuntimeInfo
    {
        public StubRuntimeInfo(DogfoodConfig config)
        {
            Provider = config.Provider;
            Model = config.Model;
        }

        public string Provider { get; }

        public string Model { get; }
    }

    /// <summary>
    /// Test double for the production report_tooling_feedback tool (SmartCon.Agent
    /// cannot be referenced from the test host). Same name/schema, same behaviour.
    /// </summary>
    private sealed class DogfoodFeedbackTool : IAgentTool
    {
        private readonly InMemoryFeedbackStore _store;
        private readonly DogfoodConfig _config;

        public DogfoodFeedbackTool(InMemoryFeedbackStore store, DogfoodConfig config)
        {
            _store = store;
            _config = config;
        }

        public AgentToolDefinition Definition { get; } = new()
        {
            Name = "report_tooling_feedback",
            Description =
                "Reports friction with SmartCon Agent itself (not with the model). Call it once " +
                "per distinct problem: inconvenient arguments, missing data, repeated calls, " +
                "unclear descriptions. area: tools | prompt | ui; rating 1 (bad) … 5 (good).",
            Risk = AgentToolRisk.Read,
            SupportsDryRun = false,
            InputSchemaJson = @"{""type"":""object"",""properties"":{""area"":{""type"":""string"",""enum"":[""tools"",""prompt"",""ui""]},""toolName"":{""type"":""string""},""rating"":{""type"":""integer"",""minimum"":1,""maximum"":5},""issue"":{""type"":""string""},""suggestion"":{""type"":""string""},""example"":{""type"":""string""}},""required"":[""area"",""rating"",""issue""],""additionalProperties"":false}"
        };

        public Task<ToolResult> ExecuteAsync(
            AgentToolContext context,
            JsonElement args,
            CancellationToken ct)
        {
            var issue = GetString(args, "issue");
            if (string.IsNullOrWhiteSpace(issue))
                return Task.FromResult(ToolResult.Fail("invalid_arguments", "issue is required"));

            var rating = Math.Max(1, Math.Min(5, GetInt(args, "rating", 3)));
            _store.RecordAsync(new AgentFeedbackEntry
            {
                DocumentId = context.DocumentId,
                Area = GetString(args, "area") ?? "tools",
                ToolName = GetString(args, "toolName"),
                Rating = rating,
                Issue = issue!,
                Suggestion = GetString(args, "suggestion"),
                Provider = _config.Provider,
                Model = _config.Model
            }, ct).GetAwaiter().GetResult();

            return Task.FromResult(ToolResult.Ok(null, "Feedback recorded.", affectedCount: 1));
        }

        private static string? GetString(JsonElement args, string property)
        {
            if (args.ValueKind != JsonValueKind.Object
                || !args.TryGetProperty(property, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return value.GetString();
        }

        private static int GetInt(JsonElement args, string property, int fallback)
        {
            if (args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var parsed))
            {
                return parsed;
            }

            return fallback;
        }
    }

    private sealed class InMemoryFeedbackStore : IAgentFeedbackStore
    {
        public List<AgentFeedbackEntry> Entries { get; } = new();

        public Task RecordAsync(AgentFeedbackEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentFeedbackEntry>> ListAsync(
            string? documentId,
            int limit,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentFeedbackEntry>>(Entries.ToList());
    }
}

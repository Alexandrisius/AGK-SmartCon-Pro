using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Eval;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent.Eval;

/// <summary>
/// Runs the 40-task eval catalog inside a real Revit process. Each task gets a
/// fresh seeded document, the production system prompt and the real read/write
/// tools; the model transcript is live-recorded or replayed from fixtures.
/// </summary>
internal sealed partial class AgentEvalRunner : IDisposable
{
    private const int MaxRounds = 6;
    private const int MaxCallsPerTask = 12;
    private const int MaxRequests = 160;

    private readonly Func<Document> _createDocument;
    private readonly EvalLlmClient _client;
    private readonly List<IAgentTool> _feedbackTools = new();
    private int _requests;

    public AgentEvalRunner(Func<Document> createDocument, EvalLlmClient client)
    {
        _createDocument = createDocument;
        _client = client;
        _feedbackTools.Add(new EvalFeedbackTool());
    }

    public async Task<AgentEvalRun> RunAsync(
        IReadOnlyList<AgentEvalTask> tasks,
        CancellationToken ct)
    {
        var startedAt = DateTime.Now;
        var results = new List<AgentEvalTaskResult>();

        foreach (var task in tasks)
        {
            ct.ThrowIfCancellationRequested();
            if (_requests >= MaxRequests)
            {
                results.Add(new AgentEvalTaskResult
                {
                    Id = task.Id,
                    Kind = task.Kind,
                    Failure = "LLM request budget reached (" + MaxRequests + ")"
                });
                continue;
            }

            var result = await RunTaskAsync(task, ct);
            results.Add(result);
        }

        var metrics = AgentEvalMetrics.Compute(tasks, results);
        var reportDirectory = EvalPaths.CreateReportDirectory();
        var report = new AgentEvalRun
        {
            StartedAt = startedAt,
            Model = _client.Model,
            Provider = _client.Provider,
            RevitVersion = string.Empty,
            Replay = _client.Replaying,
            Results = results,
            Metrics = metrics,
            ReportDirectory = reportDirectory
        };

        WriteReport(report);
        return report;
    }

    public void Dispose() => _client.Dispose();

    private async Task<AgentEvalTaskResult> RunTaskAsync(
        AgentEvalTask task,
        CancellationToken ct)
    {
        var result = new AgentEvalTaskResult { Id = task.Id, Kind = task.Kind };
        Document? document = null;
        try
        {
            document = _createDocument();
            if (string.IsNullOrEmpty(_revitVersion))
                _revitVersion = document.Application.VersionNumber;
            var transactions = new RevitTransactionService(new StubRevitContext(document));
            var seed = EvalModelSeed.Create(document, transactions);
            if (seed is null)
            {
                result.Failure = "project template has no floor-plan ViewFamilyType";
                return result;
            }

            if (!seed.WallsCreated && task.NeedsWalls())
            {
                result.Failure = "project template has no wall type";
                return result;
            }

            var toolset = CreateToolset(document, transactions, out var changeSets);
            var prompt = Substitute(task.Prompt, seed);
            ApplyRouting(task, prompt, toolset, result);
            var messages = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["role"] = "system",
                    ["content"] = AgentSystemPrompt.Build(
                        AgentProjectContextProvider.BuildDocument(document, ct),
                        AgentApprovalMode.ConfirmWrites,
                        SystemRules(task))
                },
                new() { ["role"] = "user", ["content"] = prompt }
            };

            _client.BeginTask(task);
            var answer = await RunLoopAsync(
                    task, messages, toolset, result, seed, document, changeSets, ct)
                ;
            result.Answer = answer;

            if (task.Kind == AgentEvalTaskKind.WriteApplyUndo)
                await ApplyAndUndoAsync(task, toolset, result, seed, document, ct)
                    ;

            Evaluate(task, result, seed, document);
        }
        catch (EvalReplayException ex)
        {
            result.Passed = false;
            result.Failure = ex.Message;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Failure = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            if (document is not null)
            {
                try
                {
                    var idMap = new Dictionary<string, EvalIdRef>(StringComparer.Ordinal);
                    foreach (var call in result.Calls)
                        EvalIdSupport.Collect(call.ArgumentsJson, document, idMap);
                    _client.EndTask(idMap);
                }
                catch (Exception)
                {
                    // Persisting a fixture must never mask the task result.
                }

                document.Close(false);
            }
        }

        return result;
    }

    private async Task<string?> RunLoopAsync(
        AgentEvalTask task,
        List<Dictionary<string, object?>> messages,
        IReadOnlyList<IAgentTool> toolset,
        AgentEvalTaskResult result,
        EvalModelSeed seed,
        Document document,
        InMemoryAgentChangeSetStore changeSets,
        CancellationToken ct)
    {
        var definitions = toolset.Select(tool => tool.Definition).ToList();
        var callsThisTask = 0;
        var emptyRetries = 0;
        var stopwatch = Stopwatch.StartNew();

        for (var round = 0; round < MaxRounds; round++)
        {
            if (_requests >= MaxRequests)
            {
                result.Failure = "LLM request budget reached (" + MaxRequests + ")";
                return null;
            }

            _requests++;
            result.LlmRequests++;
            var response = await _client
                .CompleteAsync(messages, definitions, ct)
                ;

            if (!string.IsNullOrEmpty(response.Error))
            {
                result.Failure = "LLM error: " + response.Error;
                return null;
            }

            result.PromptTokens += response.PromptTokens;
            result.CompletionTokens += response.CompletionTokens;
            response = RemapResponse(response);

            messages.Add(AssistantMessage(response));

            if (response.ToolCalls.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(response.Content)
                    && emptyRetries == 0
                    && round < MaxRounds - 1)
                {
                    emptyRetries++;
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = "Кратко подтверди результат: что изменилось (2–4 строки)."
                    });
                    continue;
                }

                stopwatch.Stop();
                result.LatencyMs += stopwatch.ElapsedMilliseconds;
                return response.Content ?? string.Empty;
            }

            foreach (var call in response.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                callsThisTask++;
                if (callsThisTask > MaxCallsPerTask)
                {
                    result.Failure = "tool call limit reached (" + MaxCallsPerTask + ")";
                    stopwatch.Stop();
                    result.LatencyMs += stopwatch.ElapsedMilliseconds;
                    return null;
                }

                var toolResult = await ExecuteToolCallAsync(
                        task, call, toolset, result, seed, document, changeSets, ct)
                    ;

                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call.Id,
                    ["content"] = ToolResultBudget.Serialize(toolResult)
                });
            }
        }

        stopwatch.Stop();
        result.LatencyMs += stopwatch.ElapsedMilliseconds;
        result.Failure ??= "round limit reached (" + MaxRounds + ")";
        return null;
    }

    private EvalResponseInfo RemapResponse(EvalResponseInfo response)
    {
        if (!_client.Replaying || _client.Current is null)
            return response;

        var idMap = _client.Current.IdMap;
        return new EvalResponseInfo
        {
            Content = response.Content,
            Error = response.Error,
            PromptTokens = response.PromptTokens,
            CompletionTokens = response.CompletionTokens,
            ToolCalls = response.ToolCalls.Select(call => new EvalToolCall
            {
                Id = call.Id,
                Name = call.Name,
                ArgumentsJson = call.ArgumentsJson
            }).ToList()
        };
    }

    private async Task<ToolResult> ExecuteToolCallAsync(
        AgentEvalTask task,
        EvalToolCall call,
        IReadOnlyList<IAgentTool> toolset,
        AgentEvalTaskResult result,
        EvalModelSeed seed,
        Document document,
        InMemoryAgentChangeSetStore changeSets,
        CancellationToken ct)
    {
        var tool = toolset.FirstOrDefault(candidate =>
            string.Equals(candidate.Definition.Name, call.Name, StringComparison.Ordinal));
        if (tool is null)
        {
            result.Calls.Add(new AgentEvalCall
            {
                ToolName = call.Name,
                ArgumentsJson = call.ArgumentsJson,
                Success = false,
                ErrorCode = "unknown_tool",
                SchemaValid = false
            });
            return ToolResult.Fail(
                "unknown_tool",
                "Unknown tool '" + call.Name + "'. Use only the tools listed in this request.");
        }

        var argumentsJson = call.ArgumentsJson;
        if (_client.Replaying && _client.Current is not null)
            argumentsJson = EvalIdSupport.Remap(argumentsJson, _client.Current.IdMap, document);

        var schemaErrors = AgentToolSchemaValidator.Validate(
            tool.Definition.InputSchemaJson, argumentsJson);
        var schemaValid = schemaErrors.Count == 0;

        JsonElement args;
        try
        {
            using var parsed = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            args = parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            return RecordFailure(
                result, call, argumentsJson, "invalid_arguments", schemaValid);
        }

        if (!schemaValid)
        {
            var message = "The arguments do not match the tool schema: " +
                          string.Join("; ", schemaErrors);
            result.Calls.Add(new AgentEvalCall
            {
                ToolName = call.Name,
                ArgumentsJson = argumentsJson,
                Success = false,
                ErrorCode = "invalid_arguments",
                SchemaValid = false
            });
            return ToolResult.Fail("invalid_arguments", message);
        }

        var isWrite = tool.Definition.Risk != AgentToolRisk.Read;
        var dryRun = ReadDryRun(args);
        var unauthorized = isWrite && !dryRun && task.Kind != AgentEvalTaskKind.WriteApplyUndo;
        if (unauthorized)
        {
            result.UnauthorizedWrite = true;
            var rejected = ToolResult.Fail(
                "confirmation_required",
                "Write operations require explicit user confirmation. Call the tool with " +
                "dryRun=true first; this evaluation task only allows the plan.");
            result.Calls.Add(new AgentEvalCall
            {
                ToolName = call.Name,
                ArgumentsJson = argumentsJson,
                Success = false,
                ErrorCode = "confirmation_required",
                DryRun = false
            });
            return rejected;
        }

        var context = new AgentToolContext(new ImmediateAwaitableEvent(), seed.DocumentId, Guid.Empty)
        {
            WriteApproved = isWrite
        };

        ToolResult toolResult;
        try
        {
            toolResult = await tool.ExecuteAsync(context, args, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            toolResult = ToolResult.Fail(
                "tool_exception",
                ex.GetType().Name + ": " + ex.Message);
        }

        result.Calls.Add(new AgentEvalCall
        {
            ToolName = call.Name,
            ArgumentsJson = argumentsJson,
            Success = toolResult.Success,
            ErrorCode = toolResult.ErrorCode,
            AffectedCount = toolResult.AffectedCount,
            DryRun = dryRun || !isWrite,
            SchemaValid = true,
            ResultJson = ToolResultBudget.Serialize(toolResult)
        });

        return toolResult;
    }

    private static ToolResult RecordFailure(
        AgentEvalTaskResult result,
        EvalToolCall call,
        string argumentsJson,
        string errorCode,
        bool schemaValid)
    {
        result.Calls.Add(new AgentEvalCall
        {
            ToolName = call.Name,
            ArgumentsJson = argumentsJson,
            Success = false,
            ErrorCode = errorCode,
            SchemaValid = schemaValid
        });
        return ToolResult.Fail(errorCode, "The tool call could not be parsed.");
    }

    private static bool ReadDryRun(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("dryRun", out var value))
        {
            if (value.ValueKind == JsonValueKind.False)
                return false;
            if (value.ValueKind == JsonValueKind.True)
                return true;
        }

        return true;
    }

    private static Dictionary<string, object?> AssistantMessage(EvalResponseInfo response)
    {
        var message = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = response.Content
        };

        if (response.ToolCalls.Count > 0)
        {
            message["tool_calls"] = response.ToolCalls.Select(call =>
                new Dictionary<string, object?>
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.ArgumentsJson
                    }
                }).ToList();
        }

        return message;
    }

    private List<IAgentTool> CreateToolset(
        Document document,
        RevitTransactionService transactions,
        out InMemoryAgentChangeSetStore changeSets)
    {
        var context = new StubRevitContext(document);
        changeSets = new InMemoryAgentChangeSetStore();
        return new List<IAgentTool>
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
            new GetCurrentViewInfoTool(context),
            new RenameViewsTool(context, transactions, changeSets),
            new RenameLevelsTool(context, transactions, changeSets),
            new ApplyViewTemplateTool(context, transactions, changeSets),
            new SetViewRangeTool(context, transactions, changeSets),
            new SetViewCropTool(context, transactions, changeSets),
            new SetViewVisibilityTool(context, transactions, changeSets),
            new ConfigureViewsTool(context, transactions, changeSets),
            new CopyViewSettingsTool(context, transactions, changeSets),
            new SetParametersTool(context, transactions, changeSets),
            new RemapParameterValuesTool(context, transactions, changeSets),
            new UndoLastTool(context, transactions, changeSets)
        }.Concat(_feedbackTools).ToList();
    }

    private static void ApplyRouting(
        AgentEvalTask task,
        string prompt,
        IReadOnlyList<IAgentTool> toolset,
        AgentEvalTaskResult result)
    {
        var definitions = toolset
            .Select(tool => AgentToolCatalog.Enrich(tool.Definition))
            .ToList();
        var decision = new RuleToolRouter().Route(prompt, definitions);
        result.RoutingFallback = decision.Fallback;

        foreach (var expected in task.ExpectedTools)
        {
            if (decision.Tools.Any(tool =>
                    string.Equals(tool.Name, expected, StringComparison.Ordinal)))
            {
                continue;
            }

            if (task.AlternativeTools.Count > 0
                && decision.Tools.Any(tool =>
                    task.AlternativeTools.Contains(tool.Name, StringComparer.Ordinal)))
            {
                continue;
            }

            result.RoutingHit = false;
            break;
        }
    }

    private static string SystemRules(AgentEvalTask task)
    {
        switch (task.Kind)
        {
            case AgentEvalTaskKind.WriteDryRun:
                return "- This is an evaluation run. The user asks for a change but only the " +
                       "dry run is allowed: never call a write tool with dryRun=false.\n" +
                       "- Finish with a short plan: what changes, how many items.";
            case AgentEvalTaskKind.WriteApplyUndo:
                return "- This is an evaluation run. Produce the dry-run plan for the requested " +
                       "change first; the user approves applying it afterwards.";
            default:
                return "- This is an evaluation run. Use the tools to read the model and answer " +
                       "the question in Russian; do not propose changes.";
        }
    }

    private static string Substitute(string prompt, EvalModelSeed seed)
    {
        return prompt
            .Replace("{refViewId}", seed.RefViewId.ToString())
            .Replace("{targetAId}", seed.TargetAId.ToString())
            .Replace("{targetBId}", seed.TargetBId.ToString())
            .Replace("{targetCId}", seed.TargetCId.ToString())
            .Replace("{levelId}", seed.Level1Id.ToString())
            .Replace("{level2Id}", seed.Level2Id.ToString())
            .Replace("{templateId}", seed.TemplateId.ToString())
            .Replace("{wallId}", seed.WallId.ToString())
            .Replace("{typeName}", seed.WallTypePrefix);
    }

    private sealed class EvalFeedbackTool : IAgentTool
    {
        public AgentToolDefinition Definition { get; } = new()
        {
            Name = "report_tooling_feedback",
            Description =
                "Reports friction with SmartCon Agent itself. area: tools | prompt | ui; " +
                "rating 1 (bad) … 5 (good).",
            Risk = AgentToolRisk.Read,
            SupportsDryRun = false,
            InputSchemaJson = @"{""type"":""object"",""properties"":{""area"":{""type"":""string"",""enum"":[""tools"",""prompt"",""ui""]},""toolName"":{""type"":""string""},""rating"":{""type"":""integer"",""minimum"":1,""maximum"":5},""issue"":{""type"":""string""},""suggestion"":{""type"":""string""},""example"":{""type"":""string""}},""required"":[""area"",""rating"",""issue""],""additionalProperties"":false}"
        };

        public Task<ToolResult> ExecuteAsync(
            AgentToolContext context,
            JsonElement args,
            CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok(null, "Feedback recorded.", affectedCount: 1));
    }
}

internal sealed class AgentEvalRun
{
    public DateTime StartedAt { get; init; }

    public string Model { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string RevitVersion { get; init; } = string.Empty;

    public bool Replay { get; init; }

    public IReadOnlyList<AgentEvalTaskResult> Results { get; init; } =
        Array.Empty<AgentEvalTaskResult>();

    public AgentEvalMetrics Metrics { get; init; } = new();

    public string ReportDirectory { get; init; } = string.Empty;

    public string? MarkdownPath { get; set; }

    public string? JsonPath { get; set; }
}

internal static class EvalTaskExtensions
{
    public static bool NeedsWalls(this AgentEvalTask task) =>
        task.Prompt.Contains("{wallId}", StringComparison.Ordinal)
        || task.Id.Contains("wall", StringComparison.Ordinal)
        || task.Id.Contains("comment", StringComparison.Ordinal)
        || task.Id.Contains("parameter", StringComparison.Ordinal);
}

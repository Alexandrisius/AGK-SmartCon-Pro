using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Tests.Agent.Support;

internal sealed class ImmediateAwaitableEvent : IAgentAwaitableEvent
{
    public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(funcWithApp(this));
    }

    public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        actionWithApp(this);
        return Task.CompletedTask;
    }

    public void ProcessQueue(object revitApp)
    {
    }

    public void Initialize(Action onRaise)
    {
    }
}

internal sealed class RecordingConfirmation : IAgentConfirmationService
{
    public AgentConfirmationResult Result { get; set; } = AgentConfirmationResult.Approved;

    public List<AgentConfirmationRequest> Requests { get; } = new();

    public Func<AgentConfirmationRequest, Task>? OnRequest { get; set; }

    public async Task<AgentConfirmationResult> RequestAsync(
        AgentConfirmationRequest request,
        CancellationToken ct)
    {
        Requests.Add(request);
        if (OnRequest is not null)
            await OnRequest(request).ConfigureAwait(false);

        return Result;
    }
}

internal sealed class RecordingAuditLog : IAgentAuditLog
{
    public List<AgentAuditEntry> Entries { get; } = new();

    public Task WriteAsync(AgentAuditEntry entry, CancellationToken ct)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

internal sealed class ScriptedTool : IAgentTool, IAgentToolPreflight
{
    private readonly Func<JsonElement, ToolResult>? _handler;

    public ScriptedTool(
        string name,
        AgentToolRisk risk = AgentToolRisk.Write,
        bool supportsDryRun = true,
        Func<JsonElement, ToolResult>? handler = null,
        string schema = @"{""type"":""object"",""properties"":{""items"":{""type"":""array""},""dryRun"":{""type"":""boolean""},""viewId"":{""type"":""integer""}},""additionalProperties"":false}")
    {
        _handler = handler;
        Definition = new AgentToolDefinition
        {
            Name = name,
            Description = name + " test tool",
            Risk = risk,
            SupportsDryRun = supportsDryRun,
            InputSchemaJson = schema
        };
    }

    public AgentToolDefinition Definition { get; }

    public List<(string ArgumentsJson, bool Approved)> Executions { get; } = new();

    public PlanPreflightResult PreflightResult { get; set; } = PlanPreflightResult.Pass();

    public Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        Executions.Add((args.GetRawText(), context.WriteApproved));
        if (_handler is not null)
            return Task.FromResult(_handler(args));

        var changed = 1;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            changed = items.GetArrayLength();
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            dryRun = !(args.ValueKind == JsonValueKind.Object
                       && args.TryGetProperty("dryRun", out var dryRun)
                       && dryRun.ValueKind == JsonValueKind.False),
            changed = Enumerable.Range(0, changed).Select(index => new { id = index }).ToList(),
            skipped = Array.Empty<object>(),
            failed = Array.Empty<object>(),
            total = changed,
            affected = changed
        });

        return Task.FromResult(ToolResult.Ok(payload, "applied", changed));
    }

    public Task<PlanPreflightResult> PreflightAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct) =>
        Task.FromResult(PreflightResult);
}

internal sealed class FakeToolRegistry : IToolRegistry
{
    private readonly List<IAgentTool> _tools;

    public FakeToolRegistry(params IAgentTool[] tools)
    {
        _tools = tools.ToList();
    }

    public IReadOnlyList<IAgentTool> GetAll() => _tools;

    public IReadOnlyList<AgentToolDefinition> GetDefinitions() =>
        _tools.Select(tool => tool.Definition).ToList();

    public bool TryGet(string name, out IAgentTool? tool)
    {
        tool = _tools.FirstOrDefault(
            candidate => string.Equals(candidate.Definition.Name, name, StringComparison.Ordinal));
        return tool is not null;
    }
}

internal sealed class RecordingToolRouter : IToolRouter
{
    public string? LastQuery { get; private set; }

    public int LastMaxTools { get; private set; }

    public ToolRoutingDecision Result { get; set; } = new()
    {
        Tools = Array.Empty<AgentToolDefinition>()
    };

    public Task<ToolRoutingDecision> RouteAsync(
        string query,
        IReadOnlyList<AgentToolDefinition> tools,
        int maxTools = 14,
        CancellationToken ct = default)
    {
        LastQuery = query;
        LastMaxTools = maxTools;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeRiskGate : IActionRiskGate{
    public AgentRiskDecision Decision { get; set; } = AgentRiskDecision.Confirm;

    public bool Throw { get; set; }

    public List<AgentActionRiskRequest> Requests { get; } = new();

    public Task<AgentActionRiskAssessment> AssessAsync(
        AgentActionRiskRequest request,
        CancellationToken ct)
    {
        Requests.Add(request);
        if (Throw)
            throw new InvalidOperationException("decision model unavailable");

        return Task.FromResult(new AgentActionRiskAssessment
        {
            Decision = Decision,
            Confidence = Decision == AgentRiskDecision.Auto ? 0.93 : 0.4,
            Source = "decision-model"
        });
    }
}

internal sealed class StubDecisionClient : IAgentDecisionClient
{
    public bool Enabled { get; set; } = true;

    public bool Throw { get; set; }

    public Dictionary<string, AgentDecisionAnswer> Answers { get; } =
        new(StringComparer.Ordinal);

    public List<(string State, IReadOnlyDictionary<string, AgentDecisionQuestion> Questions)>
        Requests { get; } = new();

    public string ProviderName => "stub";

    public string Model => "stub-model";

    public bool IsEnabled => Enabled;

    public Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskAsync(
        string state,
        IReadOnlyDictionary<string, AgentDecisionQuestion> questions,
        CancellationToken ct)
    {
        if (Throw)
            throw new InvalidOperationException("decision model unavailable");

        Requests.Add((state, questions));
        return Task.FromResult<IReadOnlyDictionary<string, AgentDecisionAnswer>>(Answers);
    }
}

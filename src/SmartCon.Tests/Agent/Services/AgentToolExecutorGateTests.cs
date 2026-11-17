using System.Text.Json;
using Xunit;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentToolExecutorGateTests
{
    [Fact]
    public async Task ReadTool_IsNeverConfirmed()
    {
        var confirmation = new RecordingConfirmation(AgentConfirmationResult.Rejected);
        var tool = new RecordingTool(AgentToolRisk.Read, supportsDryRun: false);
        var executor = CreateExecutor(tool, confirmation);

        var result = await executor.ExecuteAsync("recording", Args("""{"dryRun":false}"""), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(confirmation.Requests);
        Assert.True(tool.Executed);
        Assert.False(tool.LastApproved);
    }

    [Fact]
    public async Task WriteTool_DryRun_DoesNotAskForConfirmation()
    {
        var confirmation = new RecordingConfirmation(AgentConfirmationResult.Rejected);
        var tool = new RecordingTool(AgentToolRisk.Write, supportsDryRun: true);
        var executor = CreateExecutor(tool, confirmation);

        var result = await executor.ExecuteAsync("recording", Args("{}"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(confirmation.Requests);
        Assert.False(tool.LastApproved);
    }

    [Fact]
    public async Task WriteTool_ApplyWithApproval_PassesApprovedFlag()
    {
        var confirmation = new RecordingConfirmation(AgentConfirmationResult.Approved);
        var tool = new RecordingTool(AgentToolRisk.Write, supportsDryRun: true);
        var executor = CreateExecutor(tool, confirmation);

        var result = await executor.ExecuteAsync(
            "recording",
            Args("""{"dryRun":false,"items":[{"viewId":1}]}"""),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(confirmation.Requests);
        Assert.Equal("recording", confirmation.Requests[0].ToolName);
        Assert.Equal(1, confirmation.Requests[0].AffectedCount);
        Assert.False(string.IsNullOrEmpty(confirmation.Requests[0].Digest));
        Assert.True(tool.LastApproved);
    }

    [Fact]
    public async Task WriteTool_RejectedApproval_ReturnsConfirmationRejectedAndSkipsTool()
    {
        var confirmation = new RecordingConfirmation(AgentConfirmationResult.Rejected);
        var tool = new RecordingTool(AgentToolRisk.Write, supportsDryRun: true);
        var executor = CreateExecutor(tool, confirmation);

        var result = await executor.ExecuteAsync(
            "recording",
            Args("""{"dryRun":false}"""),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("confirmation_rejected", result.ErrorCode);
        Assert.False(tool.Executed);
    }

    [Fact]
    public async Task WriteTool_TimedOutApproval_ReturnsConfirmationTimedout()
    {
        var confirmation = new RecordingConfirmation(AgentConfirmationResult.TimedOut);
        var tool = new RecordingTool(AgentToolRisk.Write, supportsDryRun: true);
        var executor = CreateExecutor(tool, confirmation);

        var result = await executor.ExecuteAsync(
            "recording",
            Args("""{"dryRun":false}"""),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("confirmation_timedout", result.ErrorCode);
        Assert.False(tool.Executed);
    }

    [Fact]
    public async Task WriteTool_WithoutDryRunSupport_AlwaysConfirms()
    {
        var confirmation = new RecordingConfirmation(AgentConfirmationResult.Approved);
        var tool = new RecordingTool(AgentToolRisk.Write, supportsDryRun: false);
        var executor = CreateExecutor(tool, confirmation);

        await executor.ExecuteAsync("recording", Args("{}"), CancellationToken.None);

        Assert.Single(confirmation.Requests);
        Assert.True(tool.LastApproved);
    }

    [Fact]
    public async Task UnknownTool_ReturnsUnknownTool()
    {
        var executor = CreateExecutor(
            new RecordingTool(AgentToolRisk.Read, supportsDryRun: false),
            new RecordingConfirmation(AgentConfirmationResult.Approved));

        var result = await executor.ExecuteAsync("nope", Args("{}"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("unknown_tool", result.ErrorCode);
    }

    private static AgentToolExecutor CreateExecutor(IAgentTool tool, IAgentConfirmationService service)
    {
        var registry = new AgentToolRegistry(new[] { tool });
        return new AgentToolExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            new AgentSessionContext { DocumentId = "doc", ConversationId = Guid.Empty },
            service);
    }

    private static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class RecordingConfirmation : IAgentConfirmationService
    {
        private readonly AgentConfirmationResult _result;

        public RecordingConfirmation(AgentConfirmationResult result)
        {
            _result = result;
        }

        public List<AgentConfirmationRequest> Requests { get; } = new();

        public Task<AgentConfirmationResult> RequestAsync(
            AgentConfirmationRequest request,
            CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_result);
        }
    }

    private sealed class ImmediateAwaitableEvent : IAgentAwaitableEvent
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

    private sealed class RecordingTool : IAgentTool
    {
        public RecordingTool(AgentToolRisk risk, bool supportsDryRun)
        {
            Definition = new AgentToolDefinition
            {
                Name = "recording",
                Description = "test tool",
                Risk = risk,
                SupportsDryRun = supportsDryRun,
                InputSchemaJson = "{}"
            };
        }

        public AgentToolDefinition Definition { get; }

        public bool Executed { get; private set; }

        public bool LastApproved { get; private set; }

        public Task<ToolResult> ExecuteAsync(
            AgentToolContext context,
            JsonElement args,
            CancellationToken ct)
        {
            Executed = true;
            LastApproved = context.WriteApproved;
            return Task.FromResult(ToolResult.Ok());
        }
    }
}

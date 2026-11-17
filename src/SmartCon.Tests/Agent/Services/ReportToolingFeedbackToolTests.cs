using System.Text.Json;
using Xunit;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Agent.Services.Tools;

namespace SmartCon.Tests.Agent.Services;

public sealed class ReportToolingFeedbackToolTests
{
    [Fact]
    public async Task Execute_RecordsEntryWithSessionAndModel()
    {
        var store = new RecordingStore();
        var tool = new ReportToolingFeedbackTool(store, new StubRuntime("openrouter", "model-x"));
        var args = Parse(
            """{"area":"tools","toolName":"list_views","rating":2,"issue":"Result was truncated","suggestion":"Raise default limit"}""");

        var result = await tool.ExecuteAsync(
            new AgentToolContext(new ImmediateEvent(), "doc-1", Guid.NewGuid()),
            args,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(store.Entries);

        var entry = store.Entries[0];
        Assert.Equal("tools", entry.Area);
        Assert.Equal("list_views", entry.ToolName);
        Assert.Equal(2, entry.Rating);
        Assert.Equal("Result was truncated", entry.Issue);
        Assert.Equal("model-x", entry.Model);
        Assert.Equal("openrouter", entry.Provider);
        Assert.Equal("doc-1", entry.DocumentId);
        Assert.NotNull(entry.SessionId);
    }

    [Fact]
    public async Task Execute_WithoutIssue_ReturnsInvalidArguments()
    {
        var store = new RecordingStore();
        var tool = new ReportToolingFeedbackTool(store, new StubRuntime("p", "m"));

        var result = await tool.ExecuteAsync(
            new AgentToolContext(new ImmediateEvent(), "doc", Guid.Empty),
            Parse("""{"area":"prompt","rating":3}"""),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task Execute_UnknownArea_FallsBackToTools()
    {
        var store = new RecordingStore();
        var tool = new ReportToolingFeedbackTool(store, new StubRuntime("p", "m"));

        await tool.ExecuteAsync(
            new AgentToolContext(new ImmediateEvent(), "doc", Guid.Empty),
            Parse("""{"area":"nonsense","rating":4,"issue":"Minor"}"""),
            CancellationToken.None);

        Assert.Equal("tools", store.Entries[0].Area);
    }

    [Fact]
    public void Definition_IsReadRiskWithoutDryRun()
    {
        var tool = new ReportToolingFeedbackTool(new RecordingStore(), new StubRuntime("p", "m"));

        Assert.Equal(AgentToolRisk.Read, tool.Definition.Risk);
        Assert.False(tool.Definition.SupportsDryRun);
        Assert.Equal("report_tooling_feedback", tool.Definition.Name);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class RecordingStore : IAgentFeedbackStore
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

    private sealed class StubRuntime : IAgentRuntimeInfo
    {
        public StubRuntime(string provider, string model)
        {
            Provider = provider;
            Model = model;
        }

        public string Provider { get; }

        public string Model { get; }
    }

    private sealed class ImmediateEvent : IAgentAwaitableEvent
    {
        public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default) =>
            Task.FromResult(funcWithApp(this));

        public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
        {
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
}

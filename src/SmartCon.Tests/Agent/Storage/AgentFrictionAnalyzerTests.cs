using System.IO;
using Xunit;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services.Analysis;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Tests.Agent.Storage;

public sealed class AgentFrictionAnalyzerTests
{
    [Fact]
    public void Analyze_EmptyDatabase_ReturnsZeroAndNoFriction()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var analyzer = new AgentFrictionAnalyzer(fixture.Database);

        var report = analyzer.Analyze();

        Assert.Equal(0, report.TotalCalls);
        Assert.Equal(0, report.FailedCalls);
        Assert.Single(report.Recommendations);
        Assert.Contains("No friction", report.Recommendations[0]);
    }

    [Fact]
    public async Task Analyze_CountsErrorsTruncationAndSessions()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentToolCallStore(fixture.Database);
        var sessionA = Guid.NewGuid().ToString("D");
        var sessionB = Guid.NewGuid().ToString("D");

        await store.RecordAsync(Call("c1", sessionA, "list_views", "ok", null,
            "{\"success\":true,\"data\":{\"truncated\":true}}"), CancellationToken.None);
        await store.RecordAsync(Call("c2", sessionA, "list_views", "error", "invalid_arguments", "{}"),
            CancellationToken.None);
        await store.RecordAsync(Call("c3", sessionA, "query_elements", "error", "document_changed", "{}"),
            CancellationToken.None);
        await store.RecordAsync(Call("c4", sessionB, "query_elements", "ok", null, "{}"),
            CancellationToken.None);

        var report = new AgentFrictionAnalyzer(fixture.Database).Analyze();

        Assert.Equal(4, report.TotalCalls);
        Assert.Equal(2, report.FailedCalls);
        Assert.Equal(2, report.Sessions);
        Assert.Equal(1, report.TruncatedResults);
        Assert.Equal(1, report.InvalidArguments);
        Assert.Equal(1, report.DocumentChanged);
        Assert.Equal(2, report.Tools.Count);
        Assert.Contains(report.ErrorCodes, code => code.Code == "invalid_arguments");
        Assert.Contains(report.Recommendations, text => text.Contains("invalid_arguments"));
        Assert.Contains(report.Recommendations, text => text.Contains("truncated"));
    }

    [Fact]
    public async Task Analyze_ReadsFeedbackRating()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var feedbackStore = new SqliteAgentFeedbackStore(fixture.Database);

        await feedbackStore.RecordAsync(new AgentFeedbackEntry
        {
            Area = "tools",
            Rating = 2,
            Issue = "copy_view_settings needs a section list",
            ToolName = "copy_view_settings"
        }, CancellationToken.None);

        var report = new AgentFrictionAnalyzer(fixture.Database).Analyze();

        Assert.Equal(1, report.FeedbackEntries);
        Assert.Equal(2, report.AverageFeedbackRating);
        Assert.Contains(report.Recommendations, text => text.Contains("rating"));
    }

    [Fact]
    public async Task Markdown_ContainsTables()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentToolCallStore(fixture.Database);
        await store.RecordAsync(Call("c1", "s", "list_views", "ok", null, "{}"), CancellationToken.None);

        var markdown = new AgentFrictionAnalyzer(fixture.Database).Analyze().ToMarkdown();

        Assert.Contains("| Tool calls | 1 |", markdown);
        Assert.Contains("## Tools", markdown);
    }

    private static AgentToolCallRecord Call(
        string id,
        string session,
        string tool,
        string status,
        string? error,
        string resultJson) => new()
    {
        Id = id,
        SessionId = session,
        ToolName = tool,
        ArgumentsJson = "{}",
        ResultJson = resultJson,
        Status = status,
        Error = error,
        DurationMs = 50
    };

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _directory;

        public DatabaseFixture()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "smartcon-agent-friction-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Database = new AgentDatabase(Path.Combine(_directory, "agent.db"));
        }

        public AgentDatabase Database { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

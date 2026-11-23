using Autodesk.Revit.DB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SmartCon.Agent;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Storage;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Transactions;
using SmartCon.IntegrationTests.Support;

namespace SmartCon.IntegrationTests.Agent.Support;

/// <summary>
/// The production agent graph inside the integration-test host: the same
/// shared registration methods the application DI uses
/// (<see cref="AgentToolRegistration"/> + <see cref="AgentCoreRegistration"/>),
/// real SQLite stores in a temp database, the production decisions client and
/// the production auto-approve path. Only the Revit plumbing is doubled:
/// <see cref="StubRevitContext"/> instead of the UI context, an inline
/// <see cref="RevitThreadEvent"/> instead of the ExternalEvent pump, a
/// stub selection provider (the test host has no UI selection) and a job
/// runner driven to completion inline. No LLM call happens on construction.
/// </summary>
internal sealed class ProductionAgentHost : IDisposable
{
    // IServiceProvider instead of ServiceProvider: the test host sees two DI
    // assemblies (loose Microsoft.Extensions.DependencyInjection from the TUnit
    // stack and the ILRepack-merged SmartCon.Dependencies), so the concrete
    // type name is ambiguous at compile time.
    private readonly IServiceProvider _provider;
    private readonly IAgentConversationStore _conversationStore;
    private readonly AgentDatabase _database;
    private bool _disposed;

    private ProductionAgentHost(
        IServiceProvider provider,
        AgentLlmSettings settings,
        AgentDatabase database)
    {
        _provider = provider;
        _conversationStore = provider.GetRequiredService<IAgentConversationStore>();
        _database = database;
        Orchestrator = provider.GetRequiredService<AgentOrchestrator>();
        Settings = settings;
        Registry = provider.GetRequiredService<IToolRegistry>();
        Executor = provider.GetRequiredService<IToolExecutor>();
        Session = provider.GetRequiredService<AgentSessionContext>();
    }

    public AgentOrchestrator Orchestrator { get; }

    public AgentLlmSettings Settings { get; }

    public IToolRegistry Registry { get; }

    public IToolExecutor Executor { get; }

    public AgentSessionContext Session { get; }

    public string DatabasePath => _database.DatabasePath;

    /// <summary>
    /// The JSON artifact of the benchmark case this host served: Dispose copies
    /// the session database next to it as <c>r&lt;stamp&gt;.db</c>
    /// (SMARTCON_BENCH_KEEP_DB, on by default) so the conversation, tool calls and
    /// the audit trail survive the run. null (no artifact) keeps the old
    /// delete-on-dispose behaviour.
    /// </summary>
    public string? ArtifactJsonPath { get; set; }

    /// <summary>Same resolution as production (env first, then secrets.env) plus benchmark overrides.</summary>
    public static ProductionAgentHost Create(Document doc) => Create(doc, null);

    /// <summary>
    /// <paramref name="llmClient"/> overrides the production decision client (the
    /// last IAgentLlmClient registration wins) — used by the offline benchmark
    /// plumbing test; null keeps the real production client.
    /// </summary>
    public static ProductionAgentHost Create(Document doc, IAgentLlmClient? llmClient)
    {
        var settings = AgentLlmSettings.Load();
        ApplyBenchmarkOverrides(settings);

        var directory = Path.Combine(Path.GetTempPath(), "smartcon-bench");
        Directory.CreateDirectory(directory);
        var database = new AgentDatabase(Path.Combine(
            directory, "agent-" + Guid.NewGuid().ToString("N") + ".db"));
        database.Initialize();

        var revitContext = new StubRevitContext(doc);
        var revitThread = new RevitThreadEvent(doc.Application);
        var services = new ServiceCollection();
        services.AddSingleton<IRevitContext>(revitContext);
        // App-level service the agent tools resolve in production (registered
        // by the application outside the agent block); same implementation.
        services.AddSingleton<ITransactionService>(new RevitTransactionService(revitContext));

        var jobStore = new SqliteAgentJobStore(database);
        var jobRunner = services.AddAgentJobEngine(revitContext, jobStore);
        // The test host has no ExternalEvent pump: the tools drive the job
        // forward, mirroring the production pump handler. The pump callback is
        // raised from the tools' caller thread (the orchestrator continues on the
        // thread pool), so it is handed to the Revit thread first: production's
        // JobPumpEventHandler.Execute runs every slice on the API thread, and off
        // that thread Revit reports an untouched document as read-only — every
        // unit after the first inline one of a multi-unit family job was refused
        // with permission_denied.
        jobRunner.Initialize(() => PumpOnRevitThread(revitThread, jobRunner, doc));

        services.AddRevitAgentTools();
        services.AddAgentCore(new AgentCoreOptions
        {
            Settings = settings,
            Database = database,
            AwaitableEvent = revitThread,
            RevitContext = revitContext
        });
        if (llmClient is not null)
            services.AddSingleton<IAgentLlmClient>(llmClient);
        // Headless double of the UI-bound project context provider: the
        // production Build(object app) casts the callback to UIApplication, and
        // the RevitAPIUI JIT corrupts the UI-less test host (FileLoadException +
        // native AVE at Revit exit). Same document, DB-level builder.
        services.AddSingleton<IAgentProjectContextProvider>(
            new HeadlessProjectContextProvider(revitContext));
        services.AddSingleton<IAgentSelectionProvider>(NoSelectionProvider.Instance);

        var provider = services.BuildServiceProvider();
        var host = new ProductionAgentHost(provider, settings, database);

        // Production auto-approve path (AgentToolExecutor.Bypass): the
        // benchmark user approves every write for the whole session.
        host.Session.ApprovalMode = AgentApprovalMode.AutoApproveSession;
        return host;
    }

    public IReadOnlyList<AgentMessage> ReadConversation(Guid conversationId) =>
        _conversationStore.GetMessagesAsync(conversationId, CancellationToken.None)
            .GetAwaiter().GetResult();

    /// <summary>
    /// Pump double of <c>ExternalEvent.Raise()</c>: fire-and-forget, the slice
    /// loop runs on the Revit thread (inline when raised there, otherwise when
    /// the thread's dispatcher is pumped, as the benchmark loop does).
    /// </summary>
    private static void PumpOnRevitThread(RevitThreadEvent revitThread, JobRunner jobRunner, Document doc)
    {
        _ = revitThread
            .RaiseAsync(
                _ => jobRunner.RunToCompletion(doc, TimeSpan.FromMinutes(10)),
                CancellationToken.None)
            .ContinueWith(
                task => Console.WriteLine("ProductionAgentHost job pump failed: " + task.Exception?.Message),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
    }

    /// <summary>
    /// New conversation the way <see cref="AgentMainViewModel"/> creates one in
    /// production: a header with the real document identity and the given title.
    /// </summary>
    public Guid CreateConversation(string title)
    {
        var document = _provider.GetRequiredService<IRevitContext>().GetDocument();
        var header = new AgentConversationHeader
        {
            Title = title,
            DocumentId = AgentDocumentIdentity.GetDocumentId(document),
            DocumentPath = document.PathName,
            RevitVersion = document.Application.VersionNumber,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        return _conversationStore
            .CreateConversationAsync(header, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Confirmation audit trail from the host's own temp database: (tool, result)
    /// pairs as <see cref="SmartCon.Agent.Services.Storage.SqliteAgentAuditLog"/>
    /// writes them, e.g. ("rename_views#auto_approved", "auto_approved:bypass").
    /// </summary>
    public IReadOnlyList<(string Tool, string Result)> ReadAuditEntries()
    {
        const string query = "SELECT tool, result FROM audit_log ORDER BY ts;";

        var entries = new List<(string Tool, string Result)>();
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                entries.Add((reader.GetString(0), reader.GetString(1)));
        }

        return entries;
    }

    public IReadOnlyList<AgentToolCallRecord> ReadToolCalls(Guid conversationId)
    {
        const string query = @"
SELECT id, session_id, tool_name, arguments_json, result_json, repairs_json,
       status, error, duration_ms, created_at
FROM tool_calls
WHERE session_id = @sessionId
ORDER BY created_at;";

        var records = new List<AgentToolCallRecord>();
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = query;
            command.Parameters.AddWithValue("@sessionId", conversationId.ToString());
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                records.Add(new AgentToolCallRecord
                {
                    Id = reader.GetString(0),
                    SessionId = reader.GetString(1),
                    ToolName = reader.GetString(2),
                    ArgumentsJson = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ResultJson = reader.IsDBNull(4) ? null : reader.GetString(4),
                    RepairsJson = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Status = reader.GetString(6),
                    Error = reader.IsDBNull(7) ? null : reader.GetString(7),
                    DurationMs = reader.GetInt64(8),
                    CreatedAt = ParseTimestamp(reader.GetString(9))
                });
            }
        }

        return records;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ((IDisposable)_provider).Dispose();
        TryPreserveDatabase();
        TryDeleteDatabase();
    }

    /// <summary>
    /// Copies the session database next to the case artifact. Connections are
    /// gone by now (the provider is disposed): the journal is settled, so the
    /// copy is a complete database; the pooled handles are dropped explicitly
    /// because Microsoft.Data.Sqlite keeps them open after Dispose.
    /// </summary>
    private void TryPreserveDatabase()
    {
        var keep = Environment.GetEnvironmentVariable("SMARTCON_BENCH_KEEP_DB");
        if (string.Equals(keep, "0", StringComparison.Ordinal)
            || string.Equals(keep, "false", StringComparison.OrdinalIgnoreCase))
            return;

        if (string.IsNullOrWhiteSpace(ArtifactJsonPath) || !File.Exists(ArtifactJsonPath))
            return;

        try
        {
            SqliteConnection.ClearAllPools();
            var target = BenchRunArtifactWriter.DatabasePathFor(ArtifactJsonPath);
            File.Copy(DatabasePath, target, overwrite: true);
            Console.WriteLine("Benchmark session database: " + target);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Benchmark session database keep failed: " + ex.Message);
        }
    }

    private void TryDeleteDatabase()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                var path = DatabasePath + suffix;
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // Best effort: the file is in the temp tree.
            }
        }
    }

    private static void ApplyBenchmarkOverrides(AgentLlmSettings settings)
    {
        var model = Environment.GetEnvironmentVariable("SMARTCON_AGENT_MODEL");
        if (!string.IsNullOrWhiteSpace(model))
            settings.Model = model;

        // Same key ladder as the dogfood client; values are never logged.
        var key = Environment.GetEnvironmentVariable("SMARTCON_AGENT_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
            settings.ApiKey = key;
    }

    private static DateTime ParseTimestamp(string value) =>
        DateTime.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.UtcNow;

    /// <summary>Test double: the TUnit host has no UI selection to read.</summary>
    private sealed class NoSelectionProvider : IAgentSelectionProvider
    {
        public static readonly NoSelectionProvider Instance = new();

        public IReadOnlyList<ElementId>? GetSelectedElementIds() => null;
    }
}

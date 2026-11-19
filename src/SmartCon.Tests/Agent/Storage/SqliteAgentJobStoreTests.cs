// IJobStore completes synchronously by contract (the job runner calls it on the
// Revit API thread), so blocking on its tasks in tests cannot deadlock.
#pragma warning disable xUnit1031

using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Agent.Storage;
using Xunit;

namespace SmartCon.Tests.Agent.Storage;

public sealed class SqliteAgentJobStoreTests
{
    [Fact]
    public void Migration_FromV4_AddsJobTablesAndKeepsData()
    {
        using var fixture = new DatabaseFixture();
        CreateDatabaseUpToVersion(fixture.DatabasePath, 4);
        InsertLegacySession(fixture.DatabasePath);

        fixture.Database.Initialize();

        Assert.Equal(5, ReadUserVersion(fixture.DatabasePath));
        Assert.True(TableExists(fixture.DatabasePath, "jobs"));
        Assert.True(TableExists(fixture.DatabasePath, "job_batches"));
        Assert.True(TableExists(fixture.DatabasePath, "job_items"));
        Assert.True(TableExists(fixture.DatabasePath, "job_events"));
        Assert.Equal("Legacy", ReadSessionTitle(fixture.DatabasePath, "s1"));

        // A second Initialize is a no-op and must not create a second backup.
        fixture.Database.Initialize();
        Assert.Equal(5, ReadUserVersion(fixture.DatabasePath));
        Assert.Single(Directory.GetFiles(fixture.DirectoryPath, "*.bak-v4-*"));
    }

    [Fact]
    public void CreateAndReopen_PreservesSpecStateAndItems()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();

        var first = CreateStore(fixture);
        var job = first.CreateAsync(Spec(), CancellationToken.None).GetAwaiter().GetResult();

        first.AddUnitsAsync(
            job.JobId,
            new[]
            {
                Unit(1, "view-a", "{}"),
                Unit(2, "view-b", "{}")
            },
            CancellationToken.None).GetAwaiter().GetResult();

        first.UpdateItemsAsync(
            job.JobId,
            new[]
            {
                new JobItemUpdate
                {
                    Seq = 1,
                    State = JobItemState.Done,
                    ExpectedJson = "\"B\"",
                    ActualJson = "\"B\"",
                    UndoJson = "\"A\""
                },
                new JobItemUpdate { Seq = 2, State = JobItemState.Failed, ErrorCode = "boom" }
            },
            CancellationToken.None).GetAwaiter().GetResult();

        first.UpdateCursorAsync(job.JobId, 1, DateTime.UtcNow, CancellationToken.None)
            .GetAwaiter().GetResult();

        // A new AgentDatabase over the same file simulates a Revit restart.
        var reopened = new AgentDatabase(fixture.DatabasePath);
        reopened.Initialize();
        var store = CreateStore(fixture, reopened);

        var record = store.GetAsync(job.JobId, CancellationToken.None).GetAwaiter().GetResult();
        Assert.NotNull(record);
        Assert.Equal(job.JobId, record!.JobId);
        Assert.Equal(JobState.PreviewRunning, record.State);
        Assert.Equal("rename_views", record.Spec.Action);
        Assert.Equal(JobSavePolicy.Never, record.Spec.Options.SavePolicy);
        Assert.Equal(1, record.LastSavedCursor);
        Assert.NotNull(record.LastSavedAt);

        var items = store.LoadAllItemsAsync(job.JobId, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert.Equal(2, items.Count);
        Assert.Equal(JobItemState.Done, items[0].State);
        Assert.Equal("\"A\"", items[0].UndoJson);
        Assert.Equal(JobItemState.Failed, items[1].State);
        Assert.Equal("boom", items[1].ErrorCode);
    }

    [Fact]
    public void Transition_CompareAndSwap_RejectsStaleFrom()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = CreateStore(fixture);

        var job = store.CreateAsync(Spec(), CancellationToken.None).GetAwaiter().GetResult();

        var ok = store.TransitionAsync(
            job.JobId, JobState.PreviewRunning, JobState.PreviewReady, null, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert.Null(ok);

        var stale = store.TransitionAsync(
            job.JobId, JobState.PreviewRunning, JobState.Cancelled, null, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.NotNull(stale);
        Assert.Equal(JobErrorCodes.InvalidState, stale!.Code);

        var illegal = store.TransitionAsync(
            job.JobId, JobState.PreviewReady, JobState.Completed, null, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert.NotNull(illegal);
        Assert.Equal(JobErrorCodes.InvalidState, illegal!.Code);
    }

    [Fact]
    public void AddUnits_DuplicateUnitKey_IsRejected()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = CreateStore(fixture);
        var job = store.CreateAsync(Spec(), CancellationToken.None).GetAwaiter().GetResult();

        store.AddUnitsAsync(
            job.JobId,
            new[] { Unit(1, "same-key", "{}") },
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.Throws<SqliteException>(() =>
            store.AddUnitsAsync(
                job.JobId,
                new[] { Unit(2, "same-key", "{}") },
                CancellationToken.None).GetAwaiter().GetResult());
    }

    [Fact]
    public void ActiveJobLock_PreventsSecondActiveJobForTheDocument()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = CreateStore(fixture);

        store.CreateAsync(Spec(), CancellationToken.None).GetAwaiter().GetResult();

        Assert.Throws<SqliteException>(() =>
            store.CreateAsync(Spec(), CancellationToken.None).GetAwaiter().GetResult());

        var active = store.GetActiveAsync("doc-1", CancellationToken.None).GetAwaiter().GetResult();
        Assert.NotNull(active);
    }

    [Fact]
    public void Events_AreAppendOnlyAndOrdered()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = CreateStore(fixture);
        var job = store.CreateAsync(Spec(), CancellationToken.None).GetAwaiter().GetResult();

        store.AppendEventAsync(job.JobId, "custom-one", "{\"n\":1}", CancellationToken.None)
            .GetAwaiter().GetResult();
        store.AppendEventAsync(job.JobId, "custom-two", null, CancellationToken.None)
            .GetAwaiter().GetResult();

        var events = store.LoadEventsAsync(job.JobId, 100, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.Equal(3, events.Count);
        Assert.Equal(JobEventTypes.Created, events[0].Type);
        Assert.Equal("custom-one", events[1].Type);
        Assert.Equal("custom-two", events[2].Type);
        Assert.True(events[0].Id < events[1].Id && events[1].Id < events[2].Id);

        var one = store.GetAsync(job.JobId, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(JobState.PreviewRunning, one!.State);
    }

    [Fact]
    public void GetByPreviewDigest_FindsTheJob()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = CreateStore(fixture);
        var job = store.CreateAsync(Spec(), CancellationToken.None).GetAwaiter().GetResult();

        var preview = new JobPreview
        {
            Action = "rename_views",
            DocumentId = "doc-1",
            Count = 2,
            WillChangeCount = 2,
            SelectorHash = "selector",
            SpecHash = "spec",
            Digest = "digest-abc",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
        };
        store.UpdatePreviewAsync(job.JobId, JobJson.Serialize(preview), CancellationToken.None)
            .GetAwaiter().GetResult();

        var found = store.GetByPreviewDigestAsync("doc-1", "digest-abc", CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.NotNull(found);
        Assert.Equal(job.JobId, found!.JobId);
        Assert.Null(store.GetByPreviewDigestAsync("other-doc", "digest-abc", CancellationToken.None)
            .GetAwaiter().GetResult());
    }

    private static SqliteAgentJobStore CreateStore(DatabaseFixture fixture) =>
        new(fixture.Database);

    private static SqliteAgentJobStore CreateStore(DatabaseFixture fixture, AgentDatabase database) =>
        new(database);

    private static JobSpec Spec() => new()
    {
        DocumentId = "doc-1",
        Action = "rename_views",
        Mode = JobMode.Preview,
        SpecJson = "{\"select\":{}}",
        Options = new JobOptions
        {
            SavePolicy = JobSavePolicy.Never,
            BatchSize = 10,
            MaxItems = 100
        }
    };

    private static JobUnit Unit(int seq, string key, string args) => new()
    {
        Seq = seq,
        UnitKey = key,
        ArgsJson = args
    };

    private static void CreateDatabaseUpToVersion(string path, int version)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        foreach (var migration in AgentSchema.All
                     .Where(candidate => candidate.Version <= version)
                     .OrderBy(candidate => candidate.Version))
        {
            foreach (var statement in migration.Statements)
            {
                Execute(connection, statement.Sql, transaction);
            }

            Execute(connection, "PRAGMA user_version=" + migration.Version + ";", transaction);
        }

        transaction.Commit();
    }

    private static void InsertLegacySession(string path)
    {
        using var connection = Open(path);
        Execute(connection, @"
INSERT INTO sessions (id, document_id, title, created_at, updated_at)
    VALUES ('s1', 'doc-1', 'Legacy', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');");
    }

    private static string? ReadSessionTitle(string path, string id)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title FROM sessions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    private static int ReadUserVersion(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool TableExists(string path, string table)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection("Data Source=" + path);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.ExecuteNonQuery();
    }

    private sealed class DatabaseFixture : IDisposable
    {
        public DatabaseFixture()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "sc-agent-jobs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "agent.db");
            Database = new AgentDatabase(DatabasePath);
        }

        public string DirectoryPath { get; }

        public string DatabasePath { get; }

        public AgentDatabase Database { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
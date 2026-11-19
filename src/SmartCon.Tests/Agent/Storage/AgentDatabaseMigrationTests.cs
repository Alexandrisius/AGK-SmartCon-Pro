using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Services.Agent.Storage;
using Xunit;

namespace SmartCon.Tests.Agent.Storage;

public sealed class AgentDatabaseMigrationTests
{
    [Fact]
    public void Initialize_FreshDatabase_AppliesLatestSchema()
    {
        using var fixture = new DatabaseFixture();

        fixture.Database.Initialize();

        Assert.Equal(AgentSchema.CurrentVersion, ReadUserVersion(fixture.DatabasePath));
        Assert.True(TableExists(fixture.DatabasePath, "compactions"));
        Assert.True(TableExists(fixture.DatabasePath, "tool_result_blobs"));
        Assert.True(TableExists(fixture.DatabasePath, "working_states"));

        foreach (var column in new[]
                 {
                     "cached_tokens", "cache_write_tokens", "latency_ms",
                     "status", "request_id", "compaction_id", "estimated_prompt_tokens"
                 })
        {
            Assert.True(ColumnExists(fixture.DatabasePath, "token_usage", column));
        }

        Assert.True(ColumnExists(fixture.DatabasePath, "messages", "turn_id"));
        Assert.True(ColumnExists(fixture.DatabasePath, "tool_calls", "repairs_json"));
    }

    [Fact]
    public void Initialize_LegacyV2_CreatesBackupAndKeepsData()
    {
        using var fixture = new DatabaseFixture();
        CreateLegacyDatabase(fixture.DatabasePath, upToVersion: 2);
        InsertLegacySessionAndMessage(fixture.DatabasePath);

        fixture.Database.Initialize();

        Assert.Equal(AgentSchema.CurrentVersion, ReadUserVersion(fixture.DatabasePath));
        Assert.Equal("hello", ReadMessageContent(fixture.DatabasePath, "m1"));
        Assert.Equal(1, CountBackups(fixture.DirectoryPath, 2));
        Assert.True(TableExists(fixture.DatabasePath, "compactions"));

        fixture.Database.Initialize();

        Assert.Equal(AgentSchema.CurrentVersion, ReadUserVersion(fixture.DatabasePath));
        Assert.Equal(1, CountBackups(fixture.DirectoryPath, 2));
        Assert.Equal("hello", ReadMessageContent(fixture.DatabasePath, "m1"));
    }

    [Fact]
    public void Initialize_StaleV3WithoutNewColumn_MigratesAndSelfHeals()
    {
        using var fixture = new DatabaseFixture();
        CreateStaleV3Database(fixture.DatabasePath);
        Assert.False(ColumnExists(fixture.DatabasePath, "token_usage", "estimated_prompt_tokens"));

        fixture.Database.Initialize();

        // v4 is a real upgrade, so the backup policy applies; the missing v3
        // column is repaired by self-heal on top of the new version.
        Assert.True(ColumnExists(fixture.DatabasePath, "token_usage", "estimated_prompt_tokens"));
        Assert.Equal(AgentSchema.CurrentVersion, ReadUserVersion(fixture.DatabasePath));
        Assert.Equal(1, CountBackups(fixture.DirectoryPath, 3));

        fixture.Database.Initialize();

        Assert.True(ColumnExists(fixture.DatabasePath, "token_usage", "estimated_prompt_tokens"));
        Assert.Equal(AgentSchema.CurrentVersion, ReadUserVersion(fixture.DatabasePath));
        Assert.Equal(1, CountBackups(fixture.DirectoryPath, 3));
    }

    [Fact]
    public void Initialize_NewerSchemaVersion_LeavesDatabaseUntouched()
    {
        using var fixture = new DatabaseFixture();
        using (var connection = Open(fixture.DatabasePath))
        {
            Execute(connection, "PRAGMA user_version=99;");
        }

        fixture.Database.Initialize();

        Assert.Equal(99, ReadUserVersion(fixture.DatabasePath));
        Assert.False(TableExists(fixture.DatabasePath, "sessions"));
    }

    [Fact]
    public void Initialize_FailingMigration_RollsBackToPreviousVersion()
    {
        using var fixture = new DatabaseFixture();
        var migrations = new[]
        {
            AgentSchema.All.Single(migration => migration.Version == 1),
            new AgentSchemaMigration
            {
                Version = 2,
                Name = "broken",
                Statements = new[]
                {
                    new AgentSchemaStatement { Sql = "THIS IS NOT SQL" }
                }
            }
        };
        var database = new AgentDatabase(fixture.DatabasePath, migrations);

        Assert.Throws<SqliteException>(() => database.Initialize());

        Assert.Equal(1, ReadUserVersion(fixture.DatabasePath));
        Assert.True(TableExists(fixture.DatabasePath, "sessions"));
        Assert.False(TableExists(fixture.DatabasePath, "agent_feedback"));
    }

    private static void CreateLegacyDatabase(string databasePath, int upToVersion)
    {
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        foreach (var migration in AgentSchema.All
                     .Where(migration => migration.Version <= upToVersion)
                     .OrderBy(migration => migration.Version))
        {
            foreach (var statement in migration.Statements)
                Execute(connection, statement.Sql, transaction);

            Execute(connection, "PRAGMA user_version=" + migration.Version + ";", transaction);
        }

        transaction.Commit();
    }

    /// <summary>
    /// Simulates a database created by an intermediate build: user_version says 3
    /// but the <c>estimated_prompt_tokens</c> column is missing (MEDIUM-3).
    /// </summary>
    private static void CreateStaleV3Database(string databasePath)
    {
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        foreach (var migration in AgentSchema.All
                     .Where(migration => migration.Version <= 3)
                     .OrderBy(migration => migration.Version))
        {
            foreach (var statement in migration.Statements)
            {
                if (string.Equals(statement.Column, "estimated_prompt_tokens", StringComparison.Ordinal))
                    continue;

                Execute(connection, statement.Sql, transaction);
            }

            Execute(connection, "PRAGMA user_version=" + migration.Version + ";", transaction);
        }

        transaction.Commit();
    }

    private static void InsertLegacySessionAndMessage(string databasePath)
    {
        using var connection = Open(databasePath);
        Execute(connection, @"
INSERT INTO sessions (id, document_id, title, created_at, updated_at)
    VALUES ('s1', 'doc-1', 'Legacy', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');");
        Execute(connection, @"
INSERT INTO messages (id, session_id, role, content, created_at)
    VALUES ('m1', 's1', 'user', 'hello', '2026-01-01T00:00:00Z');");
    }

    private static int CountBackups(string directory, int fromVersion) =>
        Directory.GetFiles(directory, "*.bak-v" + fromVersion + "-*").Length;

    private static int ReadUserVersion(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string? ReadMessageContent(string databasePath, string messageId)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM messages WHERE id = $id;";
        command.Parameters.AddWithValue("$id", messageId);
        return command.ExecuteScalar() as string;
    }

    private static bool TableExists(string databasePath, string table)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static bool ColumnExists(string databasePath, string table, string column)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"" + table + "\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection("Data Source=" + databasePath);
        connection.Open();
        return connection;
    }

    private static void Execute(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (transaction is not null)
            command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private sealed class DatabaseFixture : IDisposable
    {
        public DatabaseFixture()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "smartcon-agent-migration-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "agent.db");
            Database = new AgentDatabase(DatabasePath);
        }

        public string DirectoryPath { get; }

        public string DatabasePath { get; }

        public AgentDatabase Database { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

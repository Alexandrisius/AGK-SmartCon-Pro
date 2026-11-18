using System.Globalization;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Services.Agent.Storage;
using TUnit.Core;

namespace SmartCon.IntegrationTests.Storage;

/// <summary>
/// FTS5 + bundle smoke test for the agent schema. Runs in the plain test host
/// on every platform (net48 included) without a Revit document: it validates
/// the SQLite bundle and the pure-data <see cref="AgentSchema"/>.
/// </summary>
public sealed class AgentSchemaStorageTests
{
    static AgentSchemaStorageTests()
    {
        SQLitePCL.Batteries.Init();
    }

    [Test]
    public async Task AgentSchema_AppliesAndSupportsFts5()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "smartcon-agent-schema-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "agent.db");

        try
        {
            using var connection = new SqliteConnection("Data Source=" + databasePath);
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=DELETE;";
                command.ExecuteNonQuery();
            }

            ApplySchema(connection);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version;";
                var version = Convert.ToInt32(
                    command.ExecuteScalar(), CultureInfo.InvariantCulture);
                await Assert.That(version).IsEqualTo(AgentSchema.CurrentVersion);
            }

            foreach (var table in new[] { "compactions", "tool_result_blobs", "working_states" })
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
                command.Parameters.AddWithValue("$name", table);
                var count = Convert.ToInt32(
                    command.ExecuteScalar(), CultureInfo.InvariantCulture);
                await Assert.That(count).IsEqualTo(1);
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "CREATE VIRTUAL TABLE fts_smoke USING fts5(title, body);";
                command.ExecuteNonQuery();

                command.CommandText =
                    "INSERT INTO fts_smoke (title, body) VALUES ('agent', 'BIM ops harness');";
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT COUNT(*) FROM fts_smoke WHERE fts_smoke MATCH 'agent';";
                var matches = Convert.ToInt32(
                    command.ExecuteScalar(), CultureInfo.InvariantCulture);
                await Assert.That(matches).IsEqualTo(1);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void ApplySchema(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var migration in AgentSchema.All.OrderBy(migration => migration.Version))
        {
            foreach (var statement in migration.Statements)
                Execute(connection, statement.Sql, transaction);

            Execute(connection, "PRAGMA user_version=" + migration.Version + ";", transaction);
        }

        transaction.Commit();
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
}

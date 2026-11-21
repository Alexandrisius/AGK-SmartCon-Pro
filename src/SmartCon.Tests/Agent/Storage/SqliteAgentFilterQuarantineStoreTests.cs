using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent.Storage;
using SmartCon.Core.Services.Interfaces.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Storage;

/// <summary>
/// Quarantine store of deleted view filters (#459, S.26): save/get round trip,
/// the 90-day prune-on-write retention and the v7 migration of older databases.
/// </summary>
public sealed class SqliteAgentFilterQuarantineStoreTests
{
    [Fact]
    public async Task SaveAndGet_RoundTripsTheSnapshot()
    {
        using var fixture = new QuarantineFixture();
        var snapshot = new AgentFilterSnapshot
        {
            Id = "snap-1",
            FilterName = "Вентиляция_Приток",
            UniqueId = "uid-1",
            ElementId = 123456,
            DocumentId = "doc-1",
            RulesJson = """{"type":"parameter","rules":[{"op":"Equals","value":"x"}]}""",
            CategoriesJson = """[{"categoryId":-2000011,"name":"Стены"}]""",
            AppliedViewsJson = """[{"viewId":314,"viewName":"План 1-го этажа"}]""",
            Reason = "delete_filter",
            CreatedAtUtc = DateTime.UtcNow
        };

        await fixture.Store.SaveAsync(snapshot, CancellationToken.None);
        var loaded = await fixture.Store.GetAsync("snap-1", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("Вентиляция_Приток", loaded.FilterName);
        Assert.Equal("uid-1", loaded.UniqueId);
        Assert.Equal(123456, loaded.ElementId);
        Assert.Equal("doc-1", loaded.DocumentId);
        Assert.Equal("delete_filter", loaded.Reason);
        Assert.Contains("parameter", loaded.RulesJson);

        // The payload columns must stay valid JSON: a manual restore reads them
        // with a JSON parser, not as opaque text.
        using var rules = JsonDocument.Parse(loaded.RulesJson);
        using var categories = JsonDocument.Parse(loaded.CategoriesJson);
        using var views = JsonDocument.Parse(loaded.AppliedViewsJson);
        Assert.Equal("parameter", rules.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, categories.RootElement.GetArrayLength());
        Assert.Equal(314, views.RootElement[0].GetProperty("viewId").GetInt64());
    }

    [Fact]
    public async Task Save_PrunesSnapshotsOlderThanNinetyDays()
    {
        using var fixture = new QuarantineFixture();
        fixture.InsertSnapshot("old", createdAtUtc: DateTime.UtcNow.AddDays(-91));
        Assert.NotNull(fixture.GetRaw("old"));

        var fresh = new AgentFilterSnapshot
        {
            Id = "fresh",
            FilterName = "Фильтр",
            RulesJson = "{}",
            CategoriesJson = "[]",
            AppliedViewsJson = "[]",
            CreatedAtUtc = DateTime.UtcNow
        };
        await fixture.Store.SaveAsync(fresh, CancellationToken.None);

        Assert.Null(fixture.GetRaw("old"));
        Assert.NotNull(fixture.GetRaw("fresh"));
    }

    [Fact]
    public async Task CleanupAsync_RemovesOnlyOlderSnapshots()
    {
        using var fixture = new QuarantineFixture();
        fixture.InsertSnapshot("old", DateTime.UtcNow.AddDays(-100));
        fixture.InsertSnapshot("kept", DateTime.UtcNow.AddDays(-10));

        var removed = await fixture.Store.CleanupAsync(
                DateTime.UtcNow.AddDays(-90), CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Null(fixture.GetRaw("old"));
        Assert.NotNull(fixture.GetRaw("kept"));
    }

    [Fact]
    public async Task Initialize_V6Database_MigratesToV7AndKeepsQuarantine()
    {
        using var fixture = new QuarantineFixture(createVersion: 6);
        Assert.Equal(6, fixture.ReadUserVersion());

        fixture.Database.Initialize();

        Assert.Equal(AgentSchema.CurrentVersion, fixture.ReadUserVersion());
        Assert.Equal(7, fixture.ReadUserVersion());
        Assert.True(fixture.TableExists("filter_snapshots"));

        // The migrated store is writable right away.
        var snapshot = new AgentFilterSnapshot
        {
            Id = "after-migration",
            FilterName = "Фильтр",
            RulesJson = "{}",
            CategoriesJson = "[]",
            AppliedViewsJson = "[]"
        };
        await fixture.Store.SaveAsync(snapshot, CancellationToken.None);
        Assert.NotNull(fixture.GetRaw("after-migration"));
    }

    private sealed class QuarantineFixture : IDisposable
    {
        public QuarantineFixture(int? createVersion = null)
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "smartcon-filter-quarantine-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "agent.db");
            Database = new AgentDatabase(DatabasePath);

            if (createVersion is { } version)
            {
                CreateUpTo(version);
            }
            else
            {
                Database.Initialize();
            }

            Store = new SqliteAgentFilterQuarantineStore(Database);
        }

        public string DirectoryPath { get; }

        public string DatabasePath { get; }

        public AgentDatabase Database { get; }

        public IAgentFilterQuarantineStore Store { get; }

        public void InsertSnapshot(string id, DateTime createdAtUtc)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO filter_snapshots(id, document_id, name, unique_id, element_id, rules_json,
                             categories_json, applied_views_json, reason, created_at)
VALUES(@id, NULL, @name, NULL, NULL, '{}', '[]', '[]', 'delete_filter', @createdAt);";
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@name", "Фильтр " + id);
            command.Parameters.AddWithValue(
                "@createdAt", createdAtUtc.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        public object? GetRaw(string id)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT id FROM filter_snapshots WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar();
        }

        public int ReadUserVersion()
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        public bool TableExists(string table)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
            command.Parameters.AddWithValue("$name", table);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }

        private void CreateUpTo(int version)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var migration in AgentSchema.All
                         .Where(migration => migration.Version <= version)
                         .OrderBy(migration => migration.Version))
            {
                foreach (var statement in migration.Statements)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = statement.Sql;
                    command.ExecuteNonQuery();
                }

                using var versionCommand = connection.CreateCommand();
                versionCommand.Transaction = transaction;
                versionCommand.CommandText = "PRAGMA user_version=" + migration.Version + ";";
                versionCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection("Data Source=" + DatabasePath);
            connection.Open();
            return connection;
        }

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

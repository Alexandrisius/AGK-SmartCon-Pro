using System.Globalization;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Storage;

/// <summary>
/// Quarantine of deleted view filters in agent.db (S.26, #459). Every save
/// prunes snapshots older than the 90-day retention in the same transaction
/// (the tool_result_blobs prune-on-write pattern), so no background timer is
/// needed. All calls complete synchronously inside the shared write lock.
/// </summary>
public sealed class SqliteAgentFilterQuarantineStore : IAgentFilterQuarantineStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private readonly AgentDatabase _database;

    public SqliteAgentFilterQuarantineStore(AgentDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public Task SaveAsync(AgentFilterSnapshot snapshot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

        var now = DateTime.UtcNow;
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var prune = connection.CreateCommand())
            {
                prune.Transaction = transaction;
                prune.CommandText =
                    "DELETE FROM filter_snapshots WHERE created_at < @cutoff;";
                prune.Parameters.AddWithValue(
                    "@cutoff", Format(now - Retention));
                prune.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO filter_snapshots(id, document_id, name, unique_id, element_id,
                             rules_json, categories_json, applied_views_json,
                             reason, created_at)
VALUES(@id, @documentId, @name, @uniqueId, @elementId,
       @rulesJson, @categoriesJson, @appliedViewsJson, @reason, @createdAt);";
                command.Parameters.AddWithValue("@id", snapshot.Id);
                command.Parameters.AddWithValue(
                    "@documentId", (object?)snapshot.DocumentId ?? DBNull.Value);
                command.Parameters.AddWithValue("@name", snapshot.FilterName);
                command.Parameters.AddWithValue(
                    "@uniqueId", (object?)snapshot.UniqueId ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "@elementId", (object?)snapshot.ElementId ?? DBNull.Value);
                command.Parameters.AddWithValue("@rulesJson", snapshot.RulesJson);
                command.Parameters.AddWithValue("@categoriesJson", snapshot.CategoriesJson);
                command.Parameters.AddWithValue("@appliedViewsJson", snapshot.AppliedViewsJson);
                command.Parameters.AddWithValue(
                    "@reason", (object?)snapshot.Reason ?? DBNull.Value);
                command.Parameters.AddWithValue("@createdAt", Format(snapshot.CreatedAtUtc));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    public Task<AgentFilterSnapshot?> GetAsync(string snapshotId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(snapshotId))
            throw new ArgumentException("Snapshot id is required.", nameof(snapshotId));

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT id, document_id, name, unique_id, element_id, rules_json, categories_json,
       applied_views_json, reason, created_at
FROM filter_snapshots
WHERE id = @id;";
            command.Parameters.AddWithValue("@id", snapshotId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return Task.FromResult<AgentFilterSnapshot?>(null);

            return Task.FromResult<AgentFilterSnapshot?>(new AgentFilterSnapshot
            {
                Id = reader.GetString(0),
                DocumentId = reader.IsDBNull(1) ? null : reader.GetString(1),
                FilterName = reader.GetString(2),
                UniqueId = reader.IsDBNull(3) ? null : reader.GetString(3),
                ElementId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                RulesJson = reader.GetString(5),
                CategoriesJson = reader.GetString(6),
                AppliedViewsJson = reader.GetString(7),
                Reason = reader.IsDBNull(8) ? null : reader.GetString(8),
                CreatedAtUtc = DateTime.Parse(
                    reader.GetString(9),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)
            });
        }
    }

    public Task<int> CleanupAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM filter_snapshots WHERE created_at < @cutoff;";
            command.Parameters.AddWithValue("@cutoff", Format(cutoffUtc.ToUniversalTime()));
            return Task.FromResult(command.ExecuteNonQuery());
        }
    }

    private static string Format(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

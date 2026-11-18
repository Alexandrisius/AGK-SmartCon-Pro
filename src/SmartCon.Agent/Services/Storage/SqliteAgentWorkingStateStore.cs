using System.Globalization;
using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Storage;

public sealed class SqliteAgentWorkingStateStore : IAgentWorkingStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly AgentDatabase _database;

    public SqliteAgentWorkingStateStore(AgentDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public Task<WorkingState?> LoadAsync(Guid conversationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sessionId = conversationId.ToString("D");
        string? json = null;

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT state_json FROM working_states WHERE session_id = @sessionId;";
            command.Parameters.AddWithValue("@sessionId", sessionId);
            var scalar = command.ExecuteScalar();
            if (scalar is string stored)
                json = stored;
            else if (scalar is not null && scalar is not DBNull)
                json = scalar.ToString();
        }

        if (json is null)
            return Task.FromResult<WorkingState?>(null);

        if (string.IsNullOrWhiteSpace(json))
        {
            SmartConLogger.Warn(
                "Agent working state for session " + sessionId + " is empty" +
                " [Action: the turn starts with an empty working state; the next save overwrites the row]");
            return Task.FromResult<WorkingState?>(null);
        }

        try
        {
            var state = JsonSerializer.Deserialize<WorkingState>(json, SerializerOptions);
            if (state is not null)
                NormalizeLists(state);

            return Task.FromResult(state);
        }
        catch (JsonException ex)
        {
            SmartConLogger.Warn(
                "Agent working state for session " + sessionId + " is not valid JSON: " +
                ex.GetType().Name + ": " + ex.Message +
                " [Action: the turn starts with an empty working state; the next save overwrites the row]");
            return Task.FromResult<WorkingState?>(null);
        }
    }

    public Task SaveAsync(Guid conversationId, WorkingState state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (state is null) throw new ArgumentNullException(nameof(state));

        var json = JsonSerializer.Serialize(state, SerializerOptions);
        var updatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO working_states(session_id, state_json, updated_at)
VALUES(@sessionId, @stateJson, @updatedAt)
ON CONFLICT(session_id) DO UPDATE SET
    state_json = excluded.state_json,
    updated_at = excluded.updated_at;";
            command.Parameters.AddWithValue("@sessionId", conversationId.ToString("D"));
            command.Parameters.AddWithValue("@stateJson", json);
            command.Parameters.AddWithValue("@updatedAt", updatedAt);
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Old rows (or hand-edited JSON) may carry explicit <c>null</c> for a list
    /// property; every consumer assumes non-null lists (LOW-10).
    /// </summary>
    private static void NormalizeLists(WorkingState state)
    {
        state.Constraints ??= new List<string>();
        state.CompletedSteps ??= new List<string>();
        state.PendingSteps ??= new List<string>();
        state.SelectedUniqueIds ??= new List<string>();
        state.TouchedElementIds ??= new List<string>();
        state.ChangeSetIds ??= new List<string>();
        state.OpenQuestions ??= new List<string>();
    }
}

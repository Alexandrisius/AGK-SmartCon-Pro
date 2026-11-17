using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;

namespace SmartCon.Agent.Services.Storage;

public sealed class AgentDatabase
{
    private const int SchemaVersion = 2;

    static AgentDatabase()
    {
        SQLitePCL.Batteries.Init();
    }

    public AgentDatabase()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SmartCon", "Agent", "agent.db"))
    {
    }

    public AgentDatabase(string databasePath)
    {
        DatabasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
    }

    public string DatabasePath { get; }

    public object WriteLock { get; } = new();

    public void Initialize()
    {
        lock (WriteLock)
        {
            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var connection = CreateConnection();
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=DELETE;");
            Execute(connection, "PRAGMA busy_timeout=5000;");
            Execute(connection, "PRAGMA foreign_keys=ON;");

            var version = Convert.ToInt32(ExecuteScalar(connection, "PRAGMA user_version;"));
            if (version >= SchemaVersion)
                return;

            using var transaction = connection.BeginTransaction();
            CreateSchema(connection, transaction);
            Execute(connection, "PRAGMA user_version=" + SchemaVersion + ";", transaction);
            transaction.Commit();
            SmartConLogger.Info("Agent database initialized: " + DatabasePath);
        }
    }

    public SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
            ForeignKeys = true
        };
        return new SqliteConnection(builder.ToString());
    }

    private static void CreateSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, @"
CREATE TABLE IF NOT EXISTS sessions (
    id TEXT PRIMARY KEY,
    document_id TEXT NOT NULL,
    title TEXT NOT NULL,
    document_path TEXT NULL,
    revit_version TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    token_count INTEGER NOT NULL DEFAULT 0
);", transaction);

        Execute(connection, @"
CREATE INDEX IF NOT EXISTS ix_sessions_document
    ON sessions(document_id, updated_at DESC);", transaction);

        Execute(connection, @"
CREATE TABLE IF NOT EXISTS messages (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    role TEXT NOT NULL,
    content TEXT NULL,
    reasoning_content TEXT NULL,
    tool_calls_json TEXT NULL,
    tool_call_id TEXT NULL,
    attachments_json TEXT NULL,
    created_at TEXT NOT NULL,
    token_count INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
);", transaction);

        Execute(connection, @"
CREATE INDEX IF NOT EXISTS ix_messages_session
    ON messages(session_id);", transaction);

        Execute(connection, @"
CREATE TABLE IF NOT EXISTS tool_calls (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    tool_name TEXT NOT NULL,
    arguments_json TEXT NULL,
    result_json TEXT NULL,
    status TEXT NOT NULL,
    error TEXT NULL,
    duration_ms INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL
);", transaction);

        Execute(connection, @"
CREATE INDEX IF NOT EXISTS ix_tool_calls_session
    ON tool_calls(session_id);", transaction);

        Execute(connection, @"
CREATE TABLE IF NOT EXISTS change_sets (
    id TEXT PRIMARY KEY,
    session_id TEXT NULL,
    tool_call_id TEXT NULL,
    operation TEXT NOT NULL,
    document_id TEXT NOT NULL,
    document_path TEXT NULL,
    revit_version TEXT NULL,
    author TEXT NULL,
    description TEXT NULL,
    created_at TEXT NOT NULL,
    applied_at TEXT NULL
);", transaction);

        Execute(connection, @"
CREATE INDEX IF NOT EXISTS ix_change_sets_document
    ON change_sets(document_id, created_at DESC);", transaction);

        Execute(connection, @"
CREATE TABLE IF NOT EXISTS change_items (
    id TEXT PRIMARY KEY,
    change_set_id TEXT NOT NULL,
    unique_id TEXT NULL,
    element_id INTEGER NULL,
    parameter_name TEXT NULL,
    old_value TEXT NULL,
    new_value TEXT NULL,
    status TEXT NOT NULL,
    error TEXT NULL,
    FOREIGN KEY(change_set_id) REFERENCES change_sets(id) ON DELETE CASCADE
);", transaction);

        Execute(connection, @"
CREATE INDEX IF NOT EXISTS ix_change_items_set
    ON change_items(change_set_id);", transaction);

        Execute(connection, @"
CREATE TABLE IF NOT EXISTS audit_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts TEXT NOT NULL,
    actor TEXT NULL,
    document_path TEXT NULL,
    tool TEXT NOT NULL,
    args_hash TEXT NOT NULL,
    result TEXT NOT NULL,
    affected_count INTEGER NOT NULL DEFAULT 0,
    duration_ms INTEGER NOT NULL DEFAULT 0,
    error_code TEXT NULL
);", transaction);

        Execute(connection, @"
CREATE TABLE IF NOT EXISTS token_usage (
    id TEXT PRIMARY KEY,
    session_id TEXT NULL,
    provider TEXT NULL,
    model TEXT NULL,
    prompt_tokens INTEGER NOT NULL DEFAULT 0,
    completion_tokens INTEGER NOT NULL DEFAULT 0,
    reasoning_tokens INTEGER NOT NULL DEFAULT 0,
    cost_usd TEXT NULL,
    created_at TEXT NOT NULL
);", transaction);

        // Schema v2: model feedback about its own tooling (report_tooling_feedback).
        Execute(connection, @"
CREATE TABLE IF NOT EXISTS agent_feedback (
    id TEXT PRIMARY KEY,
    session_id TEXT NULL,
    document_id TEXT NULL,
    model TEXT NULL,
    provider TEXT NULL,
    tool_name TEXT NULL,
    area TEXT NOT NULL,
    rating INTEGER NOT NULL DEFAULT 0,
    issue TEXT NOT NULL,
    suggestion TEXT NULL,
    context_json TEXT NULL,
    created_at TEXT NOT NULL
);", transaction);

        Execute(connection, @"
CREATE INDEX IF NOT EXISTS ix_agent_feedback_document
    ON agent_feedback(document_id, created_at DESC);", transaction);
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

    private static object? ExecuteScalar(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (transaction is not null)
            command.Transaction = transaction;
        return command.ExecuteScalar();
    }
}
namespace SmartCon.Core.Services.Agent.Storage;

/// <summary>
/// One DDL statement of an ordered agent database migration. Pure data: no
/// SQLite types so SmartCon.Core stays provider-agnostic.
/// </summary>
public sealed class AgentSchemaStatement
{
    public required string Sql { get; init; }

    /// <summary>
    /// Table name for an <c>ALTER TABLE ... ADD COLUMN</c> statement. Together
    /// with <see cref="Column"/> it lets the runner skip the statement when the
    /// column already exists. Null for all other DDL.
    /// </summary>
    public string? Table { get; init; }

    /// <summary>Column name for an <c>ALTER TABLE ... ADD COLUMN</c> statement.</summary>
    public string? Column { get; init; }
}

/// <summary>An ordered, versioned set of schema statements.</summary>
public sealed class AgentSchemaMigration
{
    public int Version { get; init; }

    public required string Name { get; init; }

    public IReadOnlyList<AgentSchemaStatement> Statements { get; init; } =
        Array.Empty<AgentSchemaStatement>();
}

/// <summary>
/// Single source of truth for the agent SQLite schema. Migrations are applied in
/// <see cref="Version"/> order; every migration is additive and idempotent.
/// </summary>
public static class AgentSchema
{
    public const int CurrentVersion = 5;

    public static IReadOnlyList<AgentSchemaMigration> All { get; } =
        new AgentSchemaMigration[]
        {
            new()
            {
                Version = 1,
                Name = "base",
                Statements = new[]
                {
                    Statement(@"
CREATE TABLE IF NOT EXISTS sessions (
    id TEXT PRIMARY KEY,
    document_id TEXT NOT NULL,
    title TEXT NOT NULL,
    document_path TEXT NULL,
    revit_version TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    token_count INTEGER NOT NULL DEFAULT 0
);"),
                    Statement(@"
CREATE INDEX IF NOT EXISTS ix_sessions_document
    ON sessions(document_id, updated_at DESC);"),
                    Statement(@"
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
);"),
                    Statement(@"
CREATE INDEX IF NOT EXISTS ix_messages_session
    ON messages(session_id);"),
                    Statement(@"
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
);"),
                    Statement(@"
CREATE INDEX IF NOT EXISTS ix_tool_calls_session
    ON tool_calls(session_id);"),
                    Statement(@"
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
);"),
                    Statement(@"
CREATE INDEX IF NOT EXISTS ix_change_sets_document
    ON change_sets(document_id, created_at DESC);"),
                    Statement(@"
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
);"),
                    Statement(@"
CREATE INDEX IF NOT EXISTS ix_change_items_set
    ON change_items(change_set_id);"),
                    Statement(@"
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
);"),
                    Statement(@"
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
);")
                }
            },
            new()
            {
                Version = 2,
                Name = "agent_feedback",
                Statements = new[]
                {
                    Statement(@"
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
);"),
                    Statement(@"
CREATE INDEX IF NOT EXISTS ix_agent_feedback_document
    ON agent_feedback(document_id, created_at DESC);")
                }
            },
            new()
            {
                Version = 3,
                Name = "v3a",
                Statements = new[]
                {
                    AddColumn("messages", "turn_id",
                        "ALTER TABLE messages ADD COLUMN turn_id TEXT;"),
                    Statement(@"
CREATE TABLE IF NOT EXISTS compactions (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    from_rowid INTEGER NOT NULL,
    to_rowid INTEGER NOT NULL,
    summary TEXT NOT NULL,
    tokens_before INTEGER NOT NULL,
    tokens_after INTEGER NOT NULL,
    source_prefix_hash TEXT NOT NULL,
    model TEXT,
    ack_message_id TEXT,
    created_at TEXT NOT NULL
);"),
                    Statement(@"
CREATE INDEX IF NOT EXISTS ix_compactions_session
    ON compactions(session_id, created_at);"),
                    Statement(@"
CREATE TABLE IF NOT EXISTS tool_result_blobs (
    tool_call_id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    result_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    expires_at TEXT
);"),
                    Statement(@"
CREATE INDEX IF NOT EXISTS ix_tool_result_blobs_session
    ON tool_result_blobs(session_id);"),
                    AddColumn("token_usage", "cached_tokens",
                        "ALTER TABLE token_usage ADD COLUMN cached_tokens INTEGER NOT NULL DEFAULT 0;"),
                    AddColumn("token_usage", "cache_write_tokens",
                        "ALTER TABLE token_usage ADD COLUMN cache_write_tokens INTEGER NOT NULL DEFAULT 0;"),
                    AddColumn("token_usage", "estimated_prompt_tokens",
                        "ALTER TABLE token_usage ADD COLUMN estimated_prompt_tokens INTEGER;"),
                    AddColumn("token_usage", "latency_ms",
                        "ALTER TABLE token_usage ADD COLUMN latency_ms INTEGER;"),
                    AddColumn("token_usage", "status",
                        "ALTER TABLE token_usage ADD COLUMN status TEXT NOT NULL DEFAULT 'ok';"),
                    AddColumn("token_usage", "request_id",
                        "ALTER TABLE token_usage ADD COLUMN request_id TEXT;"),
                    AddColumn("token_usage", "compaction_id",
                        "ALTER TABLE token_usage ADD COLUMN compaction_id TEXT;"),
                    AddColumn("tool_calls", "repairs_json",
                        "ALTER TABLE tool_calls ADD COLUMN repairs_json TEXT;"),
                    Statement(@"
CREATE TABLE IF NOT EXISTS working_states (
    session_id TEXT PRIMARY KEY,
    state_json TEXT NOT NULL,
    updated_at TEXT NOT NULL
);")
                }
            },
            new()
            {
                Version = 4,
                Name = "v4_context_snapshot",
                Statements = new[]
                {
                    // Last measured context fill of a conversation, so the panel
                    // restores its donut after a Revit restart and on session switch.
                    AddColumn("token_usage", "context_tokens",
                        "ALTER TABLE token_usage ADD COLUMN context_tokens INTEGER NOT NULL DEFAULT 0;"),
                    AddColumn("token_usage", "context_limit",
                        "ALTER TABLE token_usage ADD COLUMN context_limit INTEGER NOT NULL DEFAULT 0;")
                }
            },
            new()
            {
                Version = 5,
                Name = "v5_job_engine",
                Statements = new[]
                {
                    // Durable job header. spec_json carries the full JobSpec;
                    // document_id/action/mode/preview_digest are duplicated as
                    // query columns (job lock, apply lookup by digest).
                    Statement(@"
CREATE TABLE IF NOT EXISTS jobs (
    id TEXT PRIMARY KEY,
    document_id TEXT NOT NULL,
    action TEXT NOT NULL,
    mode INTEGER NOT NULL,
    spec_json TEXT NOT NULL,
    goal TEXT NULL,
    conversation_id TEXT NULL,
    state INTEGER NOT NULL,
    cancel_requested INTEGER NOT NULL DEFAULT 0,
    preview_digest TEXT NULL,
    preview_json TEXT NULL,
    error_code TEXT NULL,
    error_detail TEXT NULL,
    last_saved_cursor INTEGER NOT NULL DEFAULT 0,
    last_saved_at TEXT NULL,
    totals_json TEXT NULL,
    lease_until TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    started_at TEXT NULL,
    finished_at TEXT NULL
);"),
                    Statement(@"
CREATE INDEX IF NOT EXISTS ix_jobs_document
    ON jobs(document_id, created_at DESC);"),
                    // One active job per document (A0 job lock). The partial index
                    // includes Interrupted on purpose: a crashed job keeps the lock
                    // until it is resumed or cancelled, never silently forgotten.
                    Statement(@"
CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_active_document
    ON jobs(document_id) WHERE state IN (0, 1, 2, 3, 7, 9);"),
                    Statement(@"
CREATE INDEX IF NOT EXISTS ix_jobs_preview_digest
    ON jobs(document_id, preview_digest);"),
                    Statement(@"
CREATE TABLE IF NOT EXISTS job_batches (
    job_id TEXT NOT NULL,
    batch_index INTEGER NOT NULL,
    from_seq INTEGER NOT NULL,
    to_seq INTEGER NOT NULL,
    status TEXT NOT NULL,
    applied_count INTEGER NOT NULL DEFAULT 0,
    failed_count INTEGER NOT NULL DEFAULT 0,
    saved INTEGER NOT NULL DEFAULT 0,
    saved_at TEXT NULL,
    started_at TEXT NOT NULL,
    finished_at TEXT NULL,
    PRIMARY KEY (job_id, batch_index),
    FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
);"),
                    Statement(@"
CREATE TABLE IF NOT EXISTS job_items (
    job_id TEXT NOT NULL,
    seq INTEGER NOT NULL,
    unit_key TEXT NOT NULL,
    args_json TEXT NOT NULL,
    label TEXT NULL,
    state INTEGER NOT NULL,
    preview_json TEXT NULL,
    expected_json TEXT NULL,
    actual_json TEXT NULL,
    undo_json TEXT NULL,
    error_code TEXT NULL,
    error_detail TEXT NULL,
    attempts INTEGER NOT NULL DEFAULT 0,
    batch_index INTEGER NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (job_id, seq),
    FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
);"),
                    // Idempotency: one unit key may appear once per job (A0).
                    Statement(@"
CREATE UNIQUE INDEX IF NOT EXISTS ux_job_items_key
    ON job_items(job_id, unit_key);"),
                    Statement(@"
CREATE INDEX IF NOT EXISTS ix_job_items_state
    ON job_items(job_id, state, seq);"),
                    // Append-only audit and crash-reconciliation log.
                    Statement(@"
CREATE TABLE IF NOT EXISTS job_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id TEXT NOT NULL,
    ts TEXT NOT NULL,
    type TEXT NOT NULL,
    payload_json TEXT NULL,
    FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
);"),
                    Statement(@"
CREATE INDEX IF NOT EXISTS ix_job_events_job
    ON job_events(job_id, id);")
                }
            }
        };

    private static AgentSchemaStatement Statement(string sql) => new() { Sql = sql };

    private static AgentSchemaStatement AddColumn(string table, string column, string sql) =>
        new() { Sql = sql, Table = table, Column = column };
}

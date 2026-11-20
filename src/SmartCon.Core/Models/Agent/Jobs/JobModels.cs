namespace SmartCon.Core.Models.Agent.Jobs;

/// <summary>What a job invocation does: dry-run preview, real apply, or resume of an interrupted job.</summary>
public enum JobMode
{
    Preview = 0,
    Apply = 1,
    Resume = 2
}

/// <summary>
/// Durable job lifecycle. Persisted as the integer value, so existing numbers
/// must never be reused (append new states at the end).
/// </summary>
public enum JobState
{
    PreviewRunning = 0,
    PreviewReady = 1,
    AwaitingApproval = 2,
    Running = 3,
    Completed = 4,
    CompletedWithFailures = 5,
    Failed = 6,
    Cancelling = 7,
    Cancelled = 8,
    Interrupted = 9,
    RolledBack = 10,
    RollbackPartial = 11
}

/// <summary>
/// Per-unit lifecycle. <see cref="Previewed"/> means a dry run recorded
/// expected/actual; <see cref="Applying"/> means the undo payload is already in
/// SQLite but the change is not verified yet (the crash-recovery anchor).
/// </summary>
public enum JobItemState
{
    Pending = 0,
    Previewed = 1,
    Applying = 2,
    Done = 3,
    Skipped = 4,
    Failed = 5,
    Mismatch = 6,
    Undoing = 7,
    Undone = 8,
    UndoFailed = 9
}

public enum JobOnError
{
    Continue = 0,
    Abort = 1
}

public enum JobSavePolicy
{
    AfterEachBatch = 0,
    EveryNMinutes = 1,
    Never = 2
}

/// <summary>Explicit write scope, so a job can never leak between scopes (J-06).</summary>
public enum JobActionScope
{
    ProjectInstance = 0,
    ProjectType = 1,
    FamilyDefinition = 2,
    RfaFile = 3
}

/// <summary>Limits and policies of one job. Every limit is fail-closed.</summary>
public sealed class JobOptions
{
    /// <summary>
    /// Soft safety threshold: a job above this size needs an explicit
    /// <see cref="AllowLargeJob"/> acknowledgement at apply time.
    /// </summary>
    public const int LargeJobThreshold = 200;

    /// <summary>Units applied inside one Revit transaction / one save window.</summary>
    public int BatchSize { get; init; } = 50;

    /// <summary>Wall-clock budget of one pump slice, milliseconds (J-02: 1-2 s).</summary>
    public int SliceBudgetMs { get; init; } = 1500;

    public JobOnError OnError { get; init; } = JobOnError.Continue;

    /// <summary>Undo already applied units when the job aborts or is cancelled.</summary>
    public bool RollbackOnError { get; init; }

    public JobSavePolicy SavePolicy { get; init; } = JobSavePolicy.AfterEachBatch;

    public int SaveEveryMinutes { get; init; } = 5;

    /// <summary>Hard cap on units; the action's own <c>MaxUnits</c> is the second gate.</summary>
    public int MaxItems { get; init; } = 1000;

    /// <summary>
    /// Explicit acknowledgement that a job above <see cref="LargeJobThreshold"/>
    /// units is intended. Without it the apply/resume call is refused.
    /// </summary>
    public bool AllowLargeJob { get; init; }
}

/// <summary>
/// Apply-time policy overrides. Null means "keep the value stored by the
/// preview"; this is what lets an apply call switch on <c>onError=abort</c>
/// without silently resetting every other policy to its default.
/// </summary>
public sealed class JobOptionsOverride
{
    public JobOnError? OnError { get; init; }

    public bool? RollbackOnError { get; init; }

    public JobSavePolicy? SavePolicy { get; init; }

    public int? BatchSize { get; init; }

    public int? MaxItems { get; init; }

    public bool? AllowLargeJob { get; init; }
}

public static class JobOptionsMerger
{
    /// <summary>Applies only the explicitly supplied policies over the stored preview options.</summary>
    public static JobOptions Merge(JobOptions stored, JobOptionsOverride? overrides)
    {
        if (overrides is null)
        {
            return stored;
        }

        return new JobOptions
        {
            BatchSize = overrides.BatchSize ?? stored.BatchSize,
            SliceBudgetMs = stored.SliceBudgetMs,
            OnError = overrides.OnError ?? stored.OnError,
            RollbackOnError = overrides.RollbackOnError ?? stored.RollbackOnError,
            SavePolicy = overrides.SavePolicy ?? stored.SavePolicy,
            SaveEveryMinutes = stored.SaveEveryMinutes,
            MaxItems = overrides.MaxItems ?? stored.MaxItems,
            AllowLargeJob = overrides.AllowLargeJob ?? stored.AllowLargeJob
        };
    }
}

/// <summary>
/// Deterministic description of a job. Lives in SQLite and is the only input the
/// runner needs to continue after a restart.
/// </summary>
public sealed class JobSpec
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string DocumentId { get; init; }

    /// <summary>Registered action name, e.g. <c>rename_views</c>.</summary>
    public required string Action { get; init; }

    public JobMode Mode { get; init; } = JobMode.Preview;

    public string? Goal { get; init; }

    /// <summary>Action-specific selector/arguments, validated by the action's <c>Expand</c>.</summary>
    public string SpecJson { get; init; } = "{}";

    public JobOptions Options { get; init; } = new();

    /// <summary>
    /// Policies supplied only on this call. Apply/resume merge them over the
    /// options stored by the preview instead of replacing the whole block.
    /// </summary>
    public JobOptionsOverride? OptionsOverride { get; init; }

    /// <summary>Required for <see cref="JobMode.Apply"/>: digest of the frozen preview.</summary>
    public string? PreviewDigest { get; init; }

    /// <summary>Required for <see cref="JobMode.Resume"/>: id of the interrupted job.</summary>
    public Guid? ResumeJobId { get; init; }

    /// <summary>Conversation that started the job, for audit.</summary>
    public Guid? ConversationId { get; init; }
}

/// <summary>One frozen unit of work. Args are action-specific and immutable after expansion.</summary>
public sealed class JobUnit
{
    public int Seq { get; init; }

    /// <summary>Idempotency key: UniqueId for project units, family/type key for family units.</summary>
    public required string UnitKey { get; init; }

    public string ArgsJson { get; init; } = "{}";

    /// <summary>Human-readable label for reports and approval previews.</summary>
    public string? Label { get; init; }
}

public sealed class JobExpansion
{
    public IReadOnlyList<JobUnit> Units { get; init; } = Array.Empty<JobUnit>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public string? ErrorCode { get; init; }

    public string? ErrorDetail { get; init; }

    public bool Success => ErrorCode is null;
}

/// <summary>Dry-run result of one unit. No model change is allowed at this stage.</summary>
public sealed class JobUnitPreview
{
    /// <summary>True when the model already holds the target value (read-before-write).</summary>
    public bool Skipped { get; init; }

    public string? ExpectedJson { get; init; }

    public string? ActualJson { get; init; }

    /// <summary>
    /// Per-item undo payload computed at preview time from the read value. Persisted
    /// to SQLite before the model change (J-04); valid only while the model still
    /// matches <see cref="ActualJson"/> (guarded by the preview-stale check).
    /// </summary>
    public string? UndoJson { get; init; }

    public string? Label { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorDetail { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool Success => ErrorCode is null;
}

/// <summary>Result of applying one unit. <see cref="UndoJson"/> is the per-item undo payload.</summary>
public sealed class JobUnitApply
{
    public bool Success { get; init; }

    /// <summary>The unit was already at its target at apply time: no transaction, no write.</summary>
    public bool Skipped { get; init; }

    public string? ExpectedJson { get; init; }

    public string? UndoJson { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorDetail { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Independent read-back of one unit. <c>Verified</c> is the only source of "done".</summary>
public sealed class JobUnitVerification
{
    public bool Verified { get; init; }

    public string? ActualJson { get; init; }

    public string? MismatchReason { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorDetail { get; init; }
}

public sealed class JobUnitUndo
{
    public bool Success { get; init; }

    public bool Skipped { get; init; }

    public string? ActualJson { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorDetail { get; init; }
}

/// <summary>Context passed to action methods. Always created on the Revit thread.</summary>
public sealed class JobActionContext
{
    public required Autodesk.Revit.DB.Document Document { get; init; }

    public required string DocumentId { get; init; }

    public required Guid JobId { get; init; }

    public bool DryRun { get; init; }

    public string? SpecJson { get; init; }

    public CancellationToken CancellationToken { get; init; }
}
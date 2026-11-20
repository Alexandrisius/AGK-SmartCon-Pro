namespace SmartCon.Core.Models.Agent.Jobs;

/// <summary>Persisted per-unit row.</summary>
public sealed class JobItem
{
    public int Seq { get; init; }

    public required string UnitKey { get; init; }

    public string ArgsJson { get; init; } = "{}";

    public string? Label { get; init; }

    public JobItemState State { get; init; }

    /// <summary>Serialized <see cref="JobUnitPreview"/> produced by the dry run.</summary>
    public string? PreviewJson { get; init; }

    public string? ExpectedJson { get; init; }

    public string? ActualJson { get; init; }

    /// <summary>Per-item undo payload, written before the model change (J-04).</summary>
    public string? UndoJson { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorDetail { get; init; }

    public int Attempts { get; init; }

    public int? BatchIndex { get; init; }

    public DateTime? UpdatedAt { get; init; }
}

/// <summary>Mutable fields the runner writes back for one item.</summary>
public sealed class JobItemUpdate
{
    public int Seq { get; init; }

    public JobItemState State { get; init; }

    public string? PreviewJson { get; init; }

    public string? ExpectedJson { get; init; }

    public string? ActualJson { get; init; }

    public string? UndoJson { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorDetail { get; init; }

    public bool IncrementAttempts { get; init; }

    public int? BatchIndex { get; init; }
}

/// <summary>Aggregated counters. Rebuilt from items so the report cannot drift from the rows.</summary>
public sealed class JobTotals
{
    public int Total { get; init; }

    public int Previewed { get; init; }

    public int WillChange { get; init; }

    public int Done { get; init; }

    public int Skipped { get; init; }

    public int Failed { get; init; }

    public int Mismatch { get; init; }

    public int Undone { get; init; }

    public int UndoFailed { get; init; }

    public int Pending { get; init; }

    /// <summary>Units still in flight (Pending/Applying/Undoing): a job with these is not finished.</summary>
    public int Incomplete { get; init; }

    public static JobTotals FromItems(IReadOnlyList<JobItem> items)
    {
        var totals = new JobTotals { Total = items.Count };
        var previewed = 0;
        var willChange = 0;
        var done = 0;
        var skipped = 0;
        var failed = 0;
        var mismatch = 0;
        var undone = 0;
        var undoFailed = 0;
        var pending = 0;
        var incomplete = 0;

        foreach (var item in items)
        {
            if (item.State is JobItemState.Previewed or JobItemState.Applying or JobItemState.Done
                or JobItemState.Skipped or JobItemState.Failed or JobItemState.Mismatch
                or JobItemState.Undoing or JobItemState.Undone or JobItemState.UndoFailed)
            {
                previewed++;
            }

            switch (item.State)
            {
                case JobItemState.Pending:
                    pending++;
                    incomplete++;
                    break;
                case JobItemState.Previewed:
                    willChange++;
                    break;
                case JobItemState.Applying:
                    willChange++;
                    incomplete++;
                    break;
                case JobItemState.Done:
                    willChange++;
                    done++;
                    break;
                case JobItemState.Skipped:
                    skipped++;
                    break;
                case JobItemState.Failed:
                    failed++;
                    break;
                case JobItemState.Mismatch:
                    mismatch++;
                    break;
                case JobItemState.Undoing:
                    incomplete++;
                    break;
                case JobItemState.Undone:
                    undone++;
                    incomplete++;
                    break;
                case JobItemState.UndoFailed:
                    undoFailed++;
                    break;
            }
        }

        return new JobTotals
        {
            Total = totals.Total,
            Previewed = previewed,
            WillChange = willChange,
            Done = done,
            Skipped = skipped,
            Failed = failed,
            Mismatch = mismatch,
            Undone = undone,
            UndoFailed = undoFailed,
            Pending = pending,
            Incomplete = incomplete
        };
    }
}

/// <summary>Persisted job header.</summary>
public sealed class JobRecord
{
    public Guid JobId { get; init; }

    public required JobSpec Spec { get; init; }

    public JobState State { get; init; }

    public bool CancelRequested { get; init; }

    /// <summary>Serialized <see cref="JobPreview"/>; null until preview completes.</summary>
    public string? PreviewJson { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorDetail { get; init; }

    /// <summary>Highest seq known to be durable (a successful Document.Save happened after it).</summary>
    public int LastSavedCursor { get; init; }

    public DateTime? LastSavedAt { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    public DateTime? StartedAt { get; init; }

    public DateTime? FinishedAt { get; init; }

    public string? TotalsJson { get; init; }

    /// <summary>Lease heartbeat. An expired lease means the process died.</summary>
    public DateTime? LeaseUntil { get; init; }
}

public sealed class JobEvent
{
    public long Id { get; init; }

    public Guid JobId { get; init; }

    public DateTime At { get; init; }

    public required string Type { get; init; }

    public string? PayloadJson { get; init; }
}

public static class JobEventTypes
{
    public const string Created = "created";
    public const string ExpansionFailed = "expansion_failed";
    public const string PreviewStarted = "preview_started";
    public const string PreviewCompleted = "preview_completed";
    public const string ApplyStarted = "apply_started";
    public const string BatchStarted = "batch_started";
    public const string BatchApplied = "batch_applied";
    public const string BatchCommitted = "batch_committed";
    public const string BatchFailed = "batch_failed";
    public const string Saved = "saved";
    public const string SaveFailed = "save_failed";
    public const string CancelRequested = "cancel_requested";
    public const string Cancelled = "cancelled";
    public const string RollbackStarted = "rollback_started";
    public const string RollbackCompleted = "rollback_completed";
    public const string Interrupted = "interrupted";
    public const string Resumed = "resumed";
    public const string Reconciled = "reconciled";
    public const string Completed = "completed";
    public const string CompletedWithFailures = "completed_with_failures";
    public const string Failed = "failed";
    public const string DocumentChanged = "document_changed";
}
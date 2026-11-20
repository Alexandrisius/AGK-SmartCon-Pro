namespace SmartCon.Core.Models.Agent.Jobs;

/// <summary>
/// Operator-facing report. Built from an independent read-back, never from the
/// intent journal: <see cref="Truthful"/> is false when the model disagrees.
/// </summary>
public sealed class JobReport
{
    public Guid JobId { get; init; }

    public required string Action { get; init; }

    public required string DocumentId { get; init; }

    public JobState State { get; init; }

    public bool Saved { get; init; }

    /// <summary>True when the save policy is <c>never</c> or a save failed: progress is in memory only.</summary>
    public bool Volatile { get; init; }

    public int LastSavedCursor { get; init; }

    public DateTime? LastSavedAt { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime? FinishedAt { get; init; }

    public JobTotals Totals { get; init; } = new();

    public IReadOnlyList<JobReportItem> Items { get; init; } = Array.Empty<JobReportItem>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public string? ErrorCode { get; init; }

    public string? ErrorDetail { get; init; }

    /// <summary>False when the report's read-back found mismatches the job did not record.</summary>
    public bool Truthful { get; init; } = true;

    /// <summary>True when this report performed an independent read-back.</summary>
    public bool ReadBack { get; init; }

    /// <summary>The run was stopped by the user (cancel + optional rollback).</summary>
    public bool Cancelled { get; init; }

    /// <summary>Units that were not applied because the run was cancelled.</summary>
    public int InterruptedUnits { get; init; }

    public int MismatchCount { get; init; }

    /// <summary>Failures grouped by error code, so 500 units are not a manual scan.</summary>
    public IReadOnlyList<JobErrorGroup> Errors { get; init; } = Array.Empty<JobErrorGroup>();

    public bool HasMore { get; init; }

    public int NextOffset { get; init; }
}

public sealed class JobErrorGroup
{
    public required string ErrorCode { get; init; }

    public int Count { get; init; }

    /// <summary>First non-empty detail, enough to understand the cause without paging.</summary>
    public string? ExampleDetail { get; init; }

    public int FirstSeq { get; init; }

    public int LastSeq { get; init; }
}

public sealed class JobReportItem
{
    public int Seq { get; init; }

    public string? UnitKey { get; init; }

    public string? Label { get; init; }

    public string Status { get; init; } = string.Empty;

    public string? Expected { get; init; }

    public string? Actual { get; init; }

    public string? Undo { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorDetail { get; init; }

    public bool Verified { get; init; }

    public int Attempts { get; init; }
}

public sealed class JobRollbackResult
{
    public Guid JobId { get; init; }

    public bool Success { get; init; }

    public bool DryRun { get; init; }

    public int Restored { get; init; }

    public int RestoreFailed { get; init; }

    public int Skipped { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorDetail { get; init; }

    public JobState State { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class JobRetryResult
{
    public Guid JobId { get; init; }

    /// <summary>Units re-previewed successfully and queued for apply.</summary>
    public int Reset { get; init; }

    public int TotalFailed { get; init; }

    /// <summary>Units that failed the fresh preview again and were not queued.</summary>
    public int StillFailed { get; init; }

    /// <summary>Every failed unit that was re-evaluated by this call.</summary>
    public int Attempted { get; init; }

    /// <summary>Cause that still blocks the retry (first still-failing unit).</summary>
    public string? ErrorCode { get; init; }

    public string? ErrorDetail { get; init; }

    public JobTotals Totals { get; init; } = new();

    public JobState State { get; init; }
}
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

    public int MismatchCount { get; init; }

    public bool HasMore { get; init; }

    public int NextOffset { get; init; }
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

    public int Reset { get; init; }

    public int TotalFailed { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorDetail { get; init; }

    public JobState State { get; init; }
}
using SmartCon.Core.Models.Agent.Jobs;

namespace SmartCon.Core.Services.Interfaces.Agent;

/// <summary>
/// Durable job persistence. Implementations must be single-writer (one SQLite
/// connection at a time, DELETE journal, no WAL — I-14) and every state change
/// must be a compare-and-swap on the previous state.
/// </summary>
public interface IJobStore
{
    Task<JobRecord> CreateAsync(JobSpec spec, CancellationToken ct);

    Task<JobRecord?> GetAsync(Guid jobId, CancellationToken ct);

    /// <summary>The job that currently locks the document, if any (includes interrupted jobs).</summary>
    Task<JobRecord?> GetActiveAsync(string documentId, CancellationToken ct);

    Task<JobRecord?> GetByPreviewDigestAsync(string documentId, string digest, CancellationToken ct);

    Task<IReadOnlyList<JobRecord>> ListRecentAsync(string documentId, int limit, CancellationToken ct);

    /// <summary>
    /// Compare-and-swap: writes <paramref name="to"/> only when the stored state is
    /// still <paramref name="expectedFrom"/>. Returns a typed error otherwise.
    /// </summary>
    Task<JobError?> TransitionAsync(
        Guid jobId,
        JobState expectedFrom,
        JobState to,
        string? errorCode,
        string? errorDetail,
        CancellationToken ct);

    /// <summary>
    /// Rewrites the stored spec (mode + merged options) without touching the
    /// frozen preview or the unit rows. Used by apply/resume to persist their
    /// policy overrides so the runner and a later resume see the same contract.
    /// </summary>
    Task UpdateSpecAsync(Guid jobId, JobSpec spec, CancellationToken ct);

    Task UpdatePreviewAsync(Guid jobId, string previewJson, CancellationToken ct);

    Task UpdateTotalsAsync(Guid jobId, string totalsJson, CancellationToken ct);

    /// <summary>Called only after a successful <c>Document.Save()</c> (J-04).</summary>
    Task UpdateCursorAsync(Guid jobId, int lastSavedCursor, DateTime savedAtUtc, CancellationToken ct);

    Task SetCancelRequestedAsync(Guid jobId, bool requested, CancellationToken ct);

    Task RenewLeaseAsync(Guid jobId, DateTime leaseUntilUtc, CancellationToken ct);

    Task MarkStartedAsync(Guid jobId, DateTime startedAtUtc, CancellationToken ct);

    Task MarkFinishedAsync(
        Guid jobId,
        JobState finalState,
        string? errorCode,
        string? errorDetail,
        CancellationToken ct);

    Task AddUnitsAsync(Guid jobId, IReadOnlyList<JobUnit> units, CancellationToken ct);

    Task<IReadOnlyList<JobItem>> LoadItemsAsync(
        Guid jobId,
        IReadOnlyList<JobItemState> states,
        int afterSeq,
        int limit,
        CancellationToken ct);

    Task<IReadOnlyList<JobItem>> LoadAllItemsAsync(Guid jobId, CancellationToken ct);

    Task<int> CountItemsAsync(Guid jobId, CancellationToken ct);

    Task UpdateItemsAsync(Guid jobId, IReadOnlyList<JobItemUpdate> updates, CancellationToken ct);

    Task ResetItemsAsync(Guid jobId, IReadOnlyList<int> seqs, JobItemState to, CancellationToken ct);

    Task<int> BeginBatchAsync(Guid jobId, int fromSeq, int toSeq, CancellationToken ct);

    Task CompleteBatchAsync(
        Guid jobId,
        int batchIndex,
        int applied,
        int failed,
        bool saved,
        DateTime? savedAtUtc,
        CancellationToken ct);

    Task AppendEventAsync(Guid jobId, string type, string? payloadJson, CancellationToken ct);

    Task<IReadOnlyList<JobEvent>> LoadEventsAsync(Guid jobId, int limit, CancellationToken ct);
}
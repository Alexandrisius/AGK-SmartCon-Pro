namespace SmartCon.Core.Models.Agent.Jobs;

/// <summary>
/// The only legal state transitions of a job. Every transition goes through
/// <see cref="Validate"/>; an illegal transition is a typed error, never a silent
/// state write (the SQLite status is the resume anchor).
/// </summary>
public static class JobStateMachine
{
    private static readonly IReadOnlyDictionary<JobState, JobState[]> Transitions =
        new Dictionary<JobState, JobState[]>
        {
            [JobState.PreviewRunning] = new[]
            {
                JobState.PreviewReady, JobState.Failed, JobState.Cancelled, JobState.Interrupted
            },
            [JobState.PreviewReady] = new[]
            {
                JobState.Running, JobState.Cancelled, JobState.Failed, JobState.Interrupted
            },
            [JobState.AwaitingApproval] = new[]
            {
                JobState.Running, JobState.Cancelled, JobState.Interrupted
            },
            [JobState.Running] = new[]
            {
                JobState.Completed, JobState.CompletedWithFailures, JobState.Failed,
                JobState.Cancelling, JobState.Interrupted, JobState.RolledBack,
                JobState.RollbackPartial
            },
            [JobState.Cancelling] = new[]
            {
                JobState.Cancelled, JobState.RolledBack, JobState.RollbackPartial,
                JobState.Completed, JobState.CompletedWithFailures, JobState.Failed,
                JobState.Interrupted
            },
            [JobState.Interrupted] = new[]
            {
                JobState.Running, JobState.PreviewRunning, JobState.Cancelled, JobState.Failed,
                JobState.RolledBack, JobState.RollbackPartial, JobState.Cancelling
            },
            [JobState.Completed] = new[] { JobState.RolledBack, JobState.RollbackPartial },
            [JobState.CompletedWithFailures] = new[]
            {
                JobState.RolledBack, JobState.RollbackPartial, JobState.Running
            },
            [JobState.Failed] = new[] { JobState.Running, JobState.RolledBack, JobState.RollbackPartial },
            [JobState.Cancelled] = new[] { JobState.RolledBack, JobState.RollbackPartial },
            [JobState.RollbackPartial] = new[] { JobState.RolledBack },
            [JobState.RolledBack] = Array.Empty<JobState>()
        };

    public static IReadOnlyList<JobState> AllowedTargets(JobState from) =>
        Transitions.TryGetValue(from, out var targets) ? targets : Array.Empty<JobState>();

    public static bool CanTransition(JobState from, JobState to) =>
        Transitions.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    /// <summary>Terminal states have no background work left (rollback commands may still change them).</summary>
    public static bool IsTerminal(JobState state) =>
        state is JobState.Completed or JobState.CompletedWithFailures or JobState.Failed
            or JobState.Cancelled or JobState.RolledBack or JobState.RollbackPartial;

    /// <summary>States in which the pump must keep (or start) doing work.</summary>
    public static bool NeedsPump(JobState state) =>
        state is JobState.PreviewRunning or JobState.Running or JobState.Cancelling;

    /// <summary>True when a new job for the same document would violate the single-writer lock.</summary>
    public static bool LocksDocument(JobState state) =>
        state is JobState.PreviewRunning or JobState.PreviewReady or JobState.AwaitingApproval
            or JobState.Running or JobState.Cancelling or JobState.Interrupted;

    public static JobError? Validate(JobState from, JobState to)
    {
        if (from == to)
        {
            return JobErrors.InvalidState(
                "job is already in state '" + from + "'",
                "read the current job status and call start_job with dryRun=false (apply) or resumeJobId");
        }

        if (!CanTransition(from, to))
        {
            return JobErrors.InvalidState(
                "transition '" + from + "' -> '" + to + "' is not allowed",
                "allowed targets: " + string.Join(", ", AllowedTargets(from)) +
                "; read get_job_status and choose one of them");
        }

        return null;
    }
}
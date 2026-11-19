namespace SmartCon.Core.Models.Agent.Jobs;

/// <summary>Result of <see cref="SmartCon.Core.Services.Interfaces.Agent.IJobScheduler.Start"/>.</summary>
public sealed class JobStartResult
{
    public JobRecord? Job { get; init; }

    public JobError? Error { get; init; }

    public bool Success => Error is null && Job is not null;

    public static JobStartResult Ok(JobRecord job) => new() { Job = job };

    public static JobStartResult Fail(JobError error) => new() { Error = error };
}
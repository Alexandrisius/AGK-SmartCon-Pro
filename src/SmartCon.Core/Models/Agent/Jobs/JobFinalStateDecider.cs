namespace SmartCon.Core.Models.Agent.Jobs;

/// <summary>
/// Pure final-state decision from the rebuilt totals, so the transition is
/// unit-testable without Revit (same pattern as <c>JobSavePolicyDecider</c>).
/// </summary>
public static class JobFinalStateDecider
{
    /// <summary>
    /// Real failures win first. Only a job with zero failed/mismatch/undoFailed/
    /// incomplete units may settle as <see cref="JobState.CompletedUnverified"/>
    /// when some targets were applied but cannot be verified from the project
    /// side (#432).
    /// </summary>
    public static JobState Decide(JobTotals totals) =>
        totals.Failed > 0 || totals.Mismatch > 0 || totals.UndoFailed > 0 || totals.Incomplete > 0
            ? JobState.CompletedWithFailures
            : totals.NotVerifiable > 0
                ? JobState.CompletedUnverified
                : JobState.Completed;
}

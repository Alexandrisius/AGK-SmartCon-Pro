using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent.Jobs;

namespace SmartCon.Core.Services.Interfaces.Agent;

/// <summary>
/// One bulk operation the job engine can execute. Implementations live in
/// SmartCon.Revit and are called on the Revit API thread only.
/// <para>
/// Contract: <see cref="Expand"/> freezes the unit list; <see cref="Preview"/> is a
/// dry run that must not modify the model; <see cref="Apply"/> runs inside a
/// caller-owned transaction; <see cref="Verify"/> performs an independent read-back
/// and is the only source of "done"; <see cref="Undo"/> restores one unit from the
/// persisted undo payload.
/// </para>
/// </summary>
public interface IJobAction
{
    /// <summary>Stable tool-facing name, e.g. <c>rename_views</c>.</summary>
    string Name { get; }

    JobActionScope Scope { get; }

    /// <summary>Absolute cap on units for this action, independent of job options.</summary>
    int MaxUnits { get; }

    /// <summary>Expands the action-specific spec into frozen units. Runs once per job.</summary>
    JobExpansion Expand(JobActionContext context, string specJson);

    /// <summary>Dry run of one unit: reads the current state and computes the expected one.</summary>
    JobUnitPreview Preview(JobActionContext context, JobUnit unit);

    /// <summary>
    /// Applies one unit inside a transaction owned by the runner (project scope).
    /// Family actions manage their own documents and transactions.
    /// </summary>
    /// <param name="persistUndo">
    /// Callback the action must invoke with the undo payload before it modifies the
    /// model when the payload is only known at apply time (family backup path).
    /// </param>
    JobUnitApply Apply(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview,
        int attempt,
        Action<string>? persistUndo);

    /// <summary>Independent read-back. Never trusts <see cref="Apply"/>'s own claim.</summary>
    JobUnitVerification Verify(JobActionContext context, JobUnit unit, JobUnitPreview preview);

    /// <summary>Restores one unit using the per-item undo payload.</summary>
    JobUnitUndo Undo(JobActionContext context, JobUnit unit, string? undoJson, bool dryRun);
}

/// <summary>
/// Starts, cancels and observes jobs. Implemented on top of the job runner in
/// SmartCon.Revit; tools talk to this interface only.
/// </summary>
public interface IJobScheduler
{
    /// <summary>
    /// Creates (or resumes) a job and raises the pump. Called on the Revit thread:
    /// expansion and the first preview slice may run inline.
    /// </summary>
    JobStartResult Start(JobSpec spec, Document document);

    /// <summary>Requests cancellation; the runner stops between units (J-02).</summary>
    bool Cancel(Guid jobId, string documentId);

    /// <summary>Ensures the pump is raised when work remains (e.g. after a restart).</summary>
    void EnsurePump();
}

/// <summary>
/// Recovery surface: crash reconciliation, per-item rollback and retry of failed
/// units. Implemented by the job runner in SmartCon.Revit.
/// </summary>
public interface IJobRecovery
{
    /// <summary>Marks crashed jobs as interrupted and returns the affected job ids.</summary>
    IReadOnlyList<Guid> Reconcile(Autodesk.Revit.DB.Document document);

    JobRollbackResult Rollback(Autodesk.Revit.DB.Document document, Guid jobId, bool dryRun);

    JobRetryResult RetryFailed(Autodesk.Revit.DB.Document document, Guid jobId);
}
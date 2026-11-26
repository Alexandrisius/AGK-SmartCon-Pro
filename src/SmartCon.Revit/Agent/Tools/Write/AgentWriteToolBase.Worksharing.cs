using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Worksharing half of the write pipeline: a local workshared copy is a normal
/// editable document, but an element owned by another user rolls the whole
/// transaction back. The batch therefore takes ownership of its targets before
/// the transaction (probes them on a dry run, which must borrow nothing) and
/// skips every target that stayed unavailable.
/// </summary>
public abstract partial class AgentWriteToolBase
{
    /// <summary>
    /// Ownership gate of the current call. Never null: for a non-workshared
    /// document every check passes and the tool behaves as before.
    /// </summary>
    private protected AgentWorksharingScope WorksharingScope { get; private set; } =
        AgentWorksharingScope.None;

    /// <summary>
    /// Creation in a workshared document lands in the active workset: a tool that
    /// creates non-view elements overrides this so the workset is checked out
    /// before the transaction.
    /// </summary>
    protected virtual bool RequiresActiveWorkset => false;

    /// <summary>
    /// Targets a tool resolves itself (a type element behind scope=type, elements
    /// found by a select). The default is empty: explicit ids in the arguments are
    /// collected by the base. The ids are taken from <paramref name="document"/>
    /// before the transaction.
    /// </summary>
    protected virtual IEnumerable<ElementId> CollectWriteTargets(
        Document document,
        JsonElement args) => Array.Empty<ElementId>();

    /// <summary>Adds a per-item skip to the report (not a failure).</summary>
    private protected static void Skip(
        WriteOutcome outcome,
        WriteItemReport report,
        WorksharingBlock block)
    {
        report.ErrorCode = block.Code;
        report.Reason = block.Reason;
        outcome.Batch.Skipped.Add(report);
    }

    /// <summary>
    /// Lets a tool that drives the pipeline itself (undo_until) share one
    /// ownership scope between its per-set calls.
    /// </summary>
    internal void UseWorksharingScope(AgentWorksharingScope scope) => WorksharingScope = scope;

    /// <summary>
    /// Reports the worksharing mode and takes ownership of the batch targets.
    /// Returns a failure outcome when the central model itself is unavailable or
    /// the active workset cannot be used for creation; null means "go on".
    /// </summary>
    private WriteOutcome? PrepareWorksharing(
        Document document,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        outcome.Data["worksharing"] = WorksharingScope.ToPayload();
        if (!WorksharingScope.NeedsCentral)
            return null;

        var targets = CollectOwnershipTargets(document, args);
        if (dryRun)
            WorksharingScope.Probe(targets, ct);
        else
            WorksharingScope.Checkout(targets, ct);
        outcome.Data["worksharing"] = WorksharingScope.ToPayload();

        if (WorksharingScope.FailureCode is not null)
        {
            return FailOutcome(
                outcome,
                WorksharingScope.FailureCode,
                WorksharingScope.FailureMessage ?? "The central model is unavailable.");
        }

        if (RequiresActiveWorkset
            && !WorksharingScope.TryEnsureActiveWorkset(dryRun, out var worksetBlock))
        {
            outcome.Data["worksharing"] = WorksharingScope.ToPayload();
            return FailOutcome(
                outcome,
                WorksharingScope.FailureCode ?? worksetBlock.Code,
                WorksharingScope.FailureMessage ?? worksetBlock.Reason);
        }

        return null;
    }

    private HashSet<ElementId> CollectOwnershipTargets(Document document, JsonElement args)
    {
        var targets = new HashSet<ElementId>();
        foreach (var (_, raw) in EnumerateIdReferences(args))
            AddTarget(document, targets, ElementIdCompat.Create(raw));
        foreach (var id in CollectWriteTargets(document, args))
            AddTarget(document, targets, id);
        return targets;
    }

    private static void AddTarget(
        Document document,
        HashSet<ElementId> targets,
        ElementId? id)
    {
        if (id is null || id == ElementId.InvalidElementId)
            return;
        if (document.GetElement(id) is null)
            return;

        targets.Add(id);
    }

    private static WriteOutcome FailOutcome(
        WriteOutcome outcome,
        string errorCode,
        string message)
    {
        outcome.Success = false;
        outcome.ErrorCode = errorCode;
        outcome.Message = message;
        return outcome;
    }

    /// <summary>
    /// Explains a silent rollback of a workshared write: Revit rolls back the
    /// whole transaction when an element is owned by another user or out of date.
    /// </summary>
    private protected static void AppendWorksharingRollbackHint(
        WriteOutcome outcome,
        Document document)
    {
        if (!document.IsWorkshared)
            return;

        outcome.Message +=
            " The document is workshared: Revit rolls back the whole transaction " +
            "when an element is owned by another user or is out of date with central. " +
            "Check get_document_status, ask the owner to synchronize and relinquish, " +
            "run Reload Latest, then retry the skipped items.";
    }
}

using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Core.Services.Agent;

public sealed class AgentPlanStepResult
{
    public required string StepId { get; init; }

    public required string ToolName { get; init; }

    public bool DryRunOk { get; set; }

    public int Changed { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    public bool PreflightSkipped { get; set; }

    /// <summary>Preflight could not resolve an id that an earlier write step creates.</summary>
    public bool PreflightDeferred { get; set; }

    /// <summary>Preview deliberately did not run this step (depends on an unapplied write).</summary>
    public bool PreviewSkipped { get; set; }

    public string? PreviewSkipReason { get; set; }

    public bool FidelityChecked { get; set; }

    public bool FidelityMatched { get; set; } = true;

    public int? FidelityExpected { get; set; }

    public ToolResult? DryRunResult { get; set; }

    public ToolResult? ApplyResult { get; set; }

    public ToolResult? ReadBackResult { get; set; }

    /// <summary>True when a read-back tool was actually called for this step.</summary>
    public bool ReadBackExecuted { get; set; }

    /// <summary>Number of read-back expectations checked for this step.</summary>
    public int ExpectationsChecked { get; set; }

    public int VerifiedCount { get; set; }

    public int MismatchCount { get; set; }
}

public sealed class AgentPlanExecutionResult
{
    public bool Success { get; init; }

    public bool Approved { get; init; }

    public string? ErrorCode { get; init; }

    public string? Message { get; init; }

    public string PlanDigest { get; init; } = string.Empty;

    public IReadOnlyList<AgentPlanStepResult> Steps { get; init; } =
        Array.Empty<AgentPlanStepResult>();

    public IReadOnlyList<string> Verified { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Total read-back expectations checked. Zero with empty Verified/Mismatches
    /// means the plan had no read-back, not that the checks passed.
    /// </summary>
    public int ExpectationsChecked { get; init; }

    public int AffectedCount { get; init; }

    /// <summary>
    /// A preview that did not cover every write step because a step depends on a
    /// change that a dry run cannot commit. The apply still runs those steps.
    /// </summary>
    public bool PreviewIncomplete { get; init; }

    public IReadOnlyList<string> NotPreviewed { get; init; } = Array.Empty<string>();

    public static AgentPlanExecutionResult Fail(
        string code,
        string message,
        IReadOnlyList<AgentPlanStepResult>? steps = null) =>
        new()
        {
            Success = false,
            ErrorCode = code,
            Message = message,
            Steps = steps ?? Array.Empty<AgentPlanStepResult>()
        };
}

/// <summary>
/// Executes a validated plan (L0) as: preflight (L1) → dry run with fidelity
/// check (L4) → single plan approval with nonce/expiry/digest (L3) → apply →
/// read-back (L6). Plan-level approval replaces per-tool confirmations; the
/// tools themselves still create change sets so undo_last works.
/// </summary>
public sealed partial class AgentPlanExecutor
{
    private readonly IToolRegistry _registry;
    private readonly IAgentAwaitableEvent _revit;
    private readonly IAgentConfirmationService _confirmation;
    private readonly IAgentAuditLog? _audit;
    private readonly AgentConfirmationLedger _ledger = new();
    private readonly TimeSpan _approvalLifetime;

    public AgentPlanExecutor(
        IToolRegistry registry,
        IAgentAwaitableEvent revit,
        IAgentConfirmationService confirmation,
        IAgentAuditLog? audit = null,
        TimeSpan? approvalLifetime = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _revit = revit ?? throw new ArgumentNullException(nameof(revit));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _audit = audit;
        _approvalLifetime = approvalLifetime ?? TimeSpan.FromMinutes(5);
    }

    public async Task<AgentPlanExecutionResult> ExecuteAsync(
        AgentPlan plan,
        AgentPlanExecutionContext execution,
        CancellationToken ct)
    {
        var prepared = await PrepareAsync(plan, execution, ct).ConfigureAwait(false);
        if (prepared.Failure is not null)
            return prepared.Failure;

        var steps = prepared.Steps;
        var results = prepared.Results;

        var writeResults = results
            .Where(item => IsWrite(item.ToolName))
            .ToList();
        var affected = writeResults.Sum(item => item.Changed);
        var digest = ComputePlanDigest(plan, execution.DocumentId);

        if (writeResults.Count > 0)
        {
            var approval = await RequestApprovalAsync(plan, digest, affected, execution, ct)
                .ConfigureAwait(false);
            if (approval is not null)
                return approval;
        }

        var applyFailure = await RunApplyAsync(steps, results, execution, ct).ConfigureAwait(false);
        if (applyFailure is not null)
            return applyFailure;

        var (verified, mismatches) = await RunReadBackAsync(steps, results, execution, ct).ConfigureAwait(false);

        if (mismatches.Count > 0)
        {
            SmartConLogger.Warn(
                "Agent plan " + plan.PlanId.ToString("N") + " read-back mismatches: " +
                string.Join("; ", mismatches) +
                " [Action: review the change set with undo_last; the model state differs from the plan]");
        }

        await WriteAuditAsync(
                plan, "plan#apply", mismatches.Count == 0 ? "ok" : "mismatch", affected, execution, ct)
            .ConfigureAwait(false);

        return new AgentPlanExecutionResult
        {
            Success = true,
            Approved = true,
            PlanDigest = digest,
            Steps = results,
            Verified = verified,
            Mismatches = mismatches,
            ExpectationsChecked = results.Sum(item => item.ExpectationsChecked),
            AffectedCount = writeResults.Sum(item => item.Changed),
            PreviewIncomplete = results.Any(item => item.PreviewSkipped),
            NotPreviewed = results
                .Where(item => item.PreviewSkipped)
                .Select(item => item.StepId)
                .ToList()
        };
    }

    /// <summary>
    /// Canonical digest of the plan (what the preview showed). Callers that bind
    /// a preview to a later apply use the same value.
    /// </summary>
    public static string ComputeDigest(AgentPlan plan, string? documentId = null) =>
        ComputePlanDigest(plan, documentId);

    /// <summary>
    /// L0–L4 without approval/apply: validates the plan, runs preflight and the
    /// dry run of every write step, and returns the aggregated diff. Used by the
    /// <c>execute_plan</c> meta-tool so the model can show the plan before asking
    /// for confirmation.
    /// </summary>
    public async Task<AgentPlanExecutionResult> PreviewAsync(
        AgentPlan plan,
        AgentPlanExecutionContext execution,
        CancellationToken ct)
    {
        var prepared = await PrepareAsync(plan, execution, ct).ConfigureAwait(false);
        if (prepared.Failure is not null)
            return prepared.Failure;

        return new AgentPlanExecutionResult
        {
            Success = true,
            Approved = false,
            PlanDigest = ComputePlanDigest(plan, execution.DocumentId),
            Steps = prepared.Results,
            AffectedCount = prepared.Results
                .Where(item => IsWrite(item.ToolName))
                .Sum(item => item.Changed),
            PreviewIncomplete = prepared.Results.Any(item => item.PreviewSkipped),
            NotPreviewed = prepared.Results
                .Where(item => item.PreviewSkipped)
                .Select(item => item.StepId)
                .ToList()
        };
    }

    private async Task<(
        List<AgentPlanStep> Steps,
        List<AgentPlanStepResult> Results,
        AgentPlanExecutionResult? Failure)> PrepareAsync(
        AgentPlan plan,
        AgentPlanExecutionContext execution,
        CancellationToken ct)
    {
        if (execution is null) throw new ArgumentNullException(nameof(execution));

        if (execution.ApprovalMode == AgentApprovalMode.ReadOnly)
            return (new List<AgentPlanStep>(), new List<AgentPlanStepResult>(),
                AgentPlanExecutionResult.Fail(
                    "read_only_mode",
                    "The session is read-only. Switch the approval mode to apply the plan."));

        var validation = AgentPlanValidator.Validate(plan, _registry.GetDefinitions());
        if (!validation.IsValid)
        {
            await WriteAuditAsync(
                    plan, "plan#validate", "invalid", validation.Errors.Count, execution, ct)
                .ConfigureAwait(false);
            return (new List<AgentPlanStep>(), new List<AgentPlanStepResult>(),
                AgentPlanExecutionResult.Fail(
                    "plan_invalid",
                    string.Join("; ",
                        validation.Errors.Select(error => error.Code + ": " + error.Message))));
        }

        await WriteAuditAsync(plan, "plan#validate", "valid", 0, execution, ct)
            .ConfigureAwait(false);

        var order = AgentPlanValidator.OrderSteps(plan);
        var steps = order
            .Select(id => plan.Steps.First(step => step.StepId == id))
            .ToList();
        var results = steps
            .Select(step => new AgentPlanStepResult
            {
                StepId = step.StepId,
                ToolName = step.ToolName
            })
            .ToList();

        var preflight = await RunPreflightAsync(steps, results, execution, ct).ConfigureAwait(false);
        if (preflight is not null)
            return (steps, results, preflight);

        var dryFailure = await RunDryRunAsync(steps, results, execution, ct).ConfigureAwait(false);
        if (dryFailure is not null)
            return (steps, results, dryFailure);

        var conflict = FindStepConflict(steps, results);
        if (conflict is not null)
            return (steps, results, conflict);

        return (steps, results, null);
    }
}

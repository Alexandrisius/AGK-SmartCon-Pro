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

    /// <summary>
    /// Explains a fidelity result that is acceptable but not a literal change count
    /// (a no-op step whose targets already had the requested state).
    /// </summary>
    public string? FidelityNote { get; set; }

    public ToolResult? DryRunResult { get; set; }

    public ToolResult? ApplyResult { get; set; }

    public ToolResult? ReadBackResult { get; set; }

    /// <summary>True when a read-back tool was actually called for this step.</summary>
    public bool ReadBackExecuted { get; set; }

    /// <summary>
    /// True when the step's read-back tool was called and failed, or is not
    /// registered: nothing of this step was verified (review r1 #1).
    /// </summary>
    public bool ReadBackFailed { get; set; }

    /// <summary>Read-back expectations declared on the step.</summary>
    public int ExpectationsDeclared { get; set; }

    /// <summary>Number of read-back expectations actually compared for this step.</summary>
    public int ExpectationsChecked { get; set; }

    public int VerifiedCount { get; set; }

    public int MismatchCount { get; set; }

    /// <summary>
    /// Expectations whose path form the resolver does not understand: nothing
    /// was compared, so they are neither verified nor mismatched (C4-10).
    /// </summary>
    public int NotVerifiableCount { get; set; }
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
    /// Read-back expectations that could not be verified because their path did
    /// not address the read-back payload. Not failures: the plan applied, but
    /// these conditions must be re-checked with a proper path (C4-10).
    /// </summary>
    public IReadOnlyList<string> NotVerifiable { get; init; } = Array.Empty<string>();

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

        var (verified, mismatches, notVerifiable) = await RunReadBackAsync(
                steps, results, execution, ct).ConfigureAwait(false);

        if (mismatches.Count > 0)
        {
            SmartConLogger.Warn(
                "Agent plan " + plan.PlanId.ToString("N") + " read-back mismatches: " +
                string.Join("; ", mismatches) +
                " [Action: re-read the affected objects with the read tool; offer undo_last " +
                "only where the value really differs]");
        }

        if (notVerifiable.Count > 0)
        {
            // Declared expectations that were never compared must not pass as
            // "ok" in the audit either (review r1 #2).
            SmartConLogger.Warn(
                "Agent plan " + plan.PlanId.ToString("N") + " read-back expectation(s) not " +
                "verifiable: " + string.Join("; ", notVerifiable) +
                " [Action: re-check these values with a read tool and a supported path form]");
        }

        var auditResult = mismatches.Count > 0
            ? "mismatch"
            : notVerifiable.Count > 0
                ? "unverified"
                : "ok";
        await WriteAuditAsync(plan, "plan#apply", auditResult, affected, execution, ct)
            .ConfigureAwait(false);

        return new AgentPlanExecutionResult
        {
            Success = true,
            Approved = true,
            PlanDigest = digest,
            Steps = results,
            Verified = verified,
            Mismatches = mismatches,
            NotVerifiable = notVerifiable,
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

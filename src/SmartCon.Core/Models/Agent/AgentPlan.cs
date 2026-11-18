namespace SmartCon.Core.Models.Agent;

/// <summary>
/// A typed, reviewable multi-step plan (mvp-plan §20.5). The plan is data: it can
/// be validated (L0/L1), shown to the user, dry-run as a whole (L4), applied only
/// after approval (L3) and verified by re-reading the model (L6).
/// </summary>
public sealed class AgentPlan
{
    public Guid PlanId { get; init; } = Guid.NewGuid();

    public required string Goal { get; init; }

    public IReadOnlyList<AgentPlanStep> Steps { get; init; } = Array.Empty<AgentPlanStep>();

    /// <summary>Explicit opt-in required for Destructive tools.</summary>
    public bool AllowDestructive { get; init; }
}

public sealed class AgentPlanStep
{
    public required string StepId { get; init; }

    public required string ToolName { get; init; }

    public string ArgumentsJson { get; init; } = "{}";

    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();

    /// <summary>Requested mode; the executor forces dry runs in the plan phase.</summary>
    public bool DryRun { get; init; } = true;

    public string? Rationale { get; init; }

    /// <summary>Expected changed-item count for L4 fidelity (null = not checked).</summary>
    public int? ExpectedChanges { get; init; }

    /// <summary>Read tool used for L6 verification after apply.</summary>
    public string? ReadBackTool { get; init; }

    public string ArgumentsReadBackJson { get; init; } = "{}";

    /// <summary>Expected values in the read-back payload (dot path → exact value).</summary>
    public IReadOnlyList<AgentPlanExpectation> ReadBackExpectations { get; init; } =
        Array.Empty<AgentPlanExpectation>();
}

public sealed class AgentPlanExpectation
{
    /// <summary>Dot path inside the tool data object, e.g. "name" or "changed.0.newValue".</summary>
    public required string Path { get; init; }

    public required string Expected { get; init; }
}

public sealed class AgentPlanIssue
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public string? StepId { get; init; }
}

public sealed class AgentPlanValidationResult
{
    public IReadOnlyList<AgentPlanIssue> Errors { get; init; } = Array.Empty<AgentPlanIssue>();

    public IReadOnlyList<AgentPlanIssue> Warnings { get; init; } = Array.Empty<AgentPlanIssue>();

    public bool IsValid => Errors.Count == 0;
}

/// <summary>Result of a cheap L1 check performed before the dry run.</summary>
public sealed class PlanPreflightResult
{
    public bool Ok { get; init; }

    public string? ErrorCode { get; init; }

    public string? Message { get; init; }

    public static PlanPreflightResult Pass() => new() { Ok = true };

    public static PlanPreflightResult Fail(string errorCode, string message) =>
        new() { Ok = false, ErrorCode = errorCode, Message = message };
}

/// <summary>
/// Optional L1 preflight implemented by tools whose argument checks are cheaper
/// than a full dry run (id existence, view family compatibility, limits).
/// </summary>
public interface IAgentToolPreflight
{
    Task<PlanPreflightResult> PreflightAsync(
        AgentToolContext context,
        System.Text.Json.JsonElement args,
        CancellationToken ct);
}

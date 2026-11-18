namespace SmartCon.Core.Models.Agent;

/// <summary>
/// One typed question for a decision model (JEV / pplx-decider and other
/// System One models served by the Decisions API).
/// </summary>
public sealed class AgentDecisionQuestion
{
    public required string Type { get; init; }

    public required string Instructions { get; init; }

    /// <summary>Choice: option name → description. Noul: true/false descriptions.</summary>
    public IReadOnlyDictionary<string, string?>? Criteria { get; init; }

    public static AgentDecisionQuestion Noul(
        string instructions,
        IReadOnlyDictionary<string, string?>? criteria = null) =>
        new() { Type = "noul", Instructions = instructions, Criteria = criteria };

    public static AgentDecisionQuestion Choice(
        string instructions,
        IReadOnlyDictionary<string, string?> criteria) =>
        new() { Type = "choice", Instructions = instructions, Criteria = criteria };
}

/// <summary>Typed answer of a decision model; only the fields of its type are set.</summary>
public sealed class AgentDecisionAnswer
{
    public string Type { get; init; } = string.Empty;

    /// <summary>Probability of "yes" for a noul question (0..1).</summary>
    public double? Noul { get; init; }

    public string? Choice { get; init; }

    public IReadOnlyDictionary<string, double>? Probabilities { get; init; }

    public double? Confidence { get; init; }

    /// <summary>Set when the provider refused to answer or returned an unknown type.</summary>
    public string? Error { get; init; }

    public bool IsAnswered => Error is null && Type is "noul" or "choice" or "score";
}

/// <summary>
/// Optional decision-model client. Implementations are fail-closed: any
/// transport/protocol error surfaces as an error answer or an exception that
/// the caller treats as "unavailable".
/// </summary>
public interface IAgentDecisionClient
{
    string ProviderName { get; }

    string Model { get; }

    bool IsEnabled { get; }

    Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskAsync(
        string state,
        IReadOnlyDictionary<string, AgentDecisionQuestion> questions,
        CancellationToken ct);
}

public enum AgentRiskDecision
{
    /// <summary>Ask the user (fail-closed default).</summary>
    Confirm = 0,

    /// <summary>The decision model is confident that the action is safe to auto-apply.</summary>
    Auto = 1
}

public sealed class AgentActionRiskRequest
{
    public required string ToolName { get; init; }

    public AgentToolRisk Risk { get; init; }

    public int AffectedCount { get; init; }

    public bool Reversible { get; init; } = true;

    /// <summary>Argument shape only (keys/types), never element names or values.</summary>
    public string ArgumentSummary { get; init; } = string.Empty;
}

public sealed class AgentActionRiskAssessment
{
    public AgentRiskDecision Decision { get; init; } = AgentRiskDecision.Confirm;

    public double Confidence { get; init; }

    public string? Rationale { get; init; }

    /// <summary>"decision-model", "unavailable", "disabled".</summary>
    public string Source { get; init; } = "unavailable";
}

/// <summary>
/// Optional risk gate for the bypass mode. Must fail closed: unknown, refused
/// or low-confidence answers resolve to <see cref="AgentRiskDecision.Confirm"/>.
/// </summary>
public interface IActionRiskGate
{
    Task<AgentActionRiskAssessment> AssessAsync(
        AgentActionRiskRequest request,
        CancellationToken ct);
}

using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services;

/// <summary>
/// Production entry point for the system prompt. The prompt text itself lives in
/// <see cref="AgentSystemPrompt"/> (Core) so the eval harness can send exactly the
/// same instructions.
/// </summary>
public sealed class AgentPromptBuilder
{
    /// <summary>
    /// Stable prefix of every request: no document, mode or counter state, so
    /// the provider cache keeps it across turns (P.3).
    /// </summary>
    public string BuildStatic() => AgentSystemPrompt.BuildStatic();

    public string Build(
        AgentProjectContext context,
        AgentApprovalMode mode = AgentApprovalMode.ConfirmWrites,
        string? extraRules = null) =>
        AgentSystemPrompt.Build(context, mode, extraRules);
}

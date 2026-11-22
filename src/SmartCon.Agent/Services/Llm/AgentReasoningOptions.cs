namespace SmartCon.Agent.Services.Llm;

/// <summary>
/// Effective reasoning request fields of one request: the API-format shape and
/// the effort value the provider currently accepts (after any degradation).
/// </summary>
internal sealed record AgentReasoningOptions(AgentReasoningFormat Format, string Effort);

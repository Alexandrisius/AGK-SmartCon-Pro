namespace SmartCon.Core.Services.Interfaces.Agent;

/// <summary>Summarizes a transcript chunk; the production implementation calls the LLM.</summary>
public interface IAgentSummarizer
{
    Task<string> SummarizeAsync(string input, int maxOutputTokens, CancellationToken ct);
}

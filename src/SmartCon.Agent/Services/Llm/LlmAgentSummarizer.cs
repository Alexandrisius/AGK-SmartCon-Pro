using System.Text;
using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Llm;

/// <summary>
/// Production summarizer: calls the configured LLM (cheap model when
/// <see cref="AgentLlmSettings.SummaryModel"/> is set) and returns the
/// nine-section checkpoint required by runbook H1-16.
/// </summary>
public sealed class LlmAgentSummarizer : IAgentSummarizer
{
    private const string Instructions =
@"You compress the earlier part of a Revit assistant conversation into a checkpoint.
Preserve exactly: ElementIds, UniqueIds, view and level names, parameter names, changeSetIds, planDigest values, user constraints and decisions, tool error codes, open questions.
Never invent ids or names. Keep the user's original wording for constraints.
Return only the checkpoint, no preamble, at most the requested number of tokens, using these nine sections:
1. Goal
2. Constraints and decisions
3. Done
4. In progress
5. Pending
6. Key facts and IDs
7. Tool results and artifacts (keep resultHandle values)
8. Errors and risks
9. Next step";

    private readonly IAgentLlmClient _llm;
    private readonly AgentLlmSettings _settings;

    public LlmAgentSummarizer(IAgentLlmClient llm, AgentLlmSettings settings)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<string> SummarizeAsync(
        string input,
        int maxOutputTokens,
        CancellationToken ct)
    {
        var request = new AgentChatRequest
        {
            Model = string.IsNullOrWhiteSpace(_settings.SummaryModel)
                ? _settings.Model
                : _settings.SummaryModel,
            Messages = new[]
            {
                AgentMessage.System(Instructions),
                AgentMessage.User(input)
            },
            Temperature = 0.1,
            MaxOutputTokens = Math.Max(256, maxOutputTokens)
        };

        var builder = new StringBuilder();
        await foreach (var streamEvent in _llm.StreamAsync(request, ct).ConfigureAwait(false))
        {
            switch (streamEvent.Kind)
            {
                case LlmStreamEventKind.TextDelta:
                    builder.Append(streamEvent.Text);
                    break;

                case LlmStreamEventKind.Error:
                    throw new AgentLlmException(
                        streamEvent.Error ?? "Summary stream error.");

                case LlmStreamEventKind.Completed:
                    var completedText = streamEvent.Completion?.Message?.Content;
                    if (builder.Length == 0 && !string.IsNullOrWhiteSpace(completedText))
                        builder.Append(completedText);
                    break;
            }
        }

        var summary = builder.ToString().Trim();
        if (summary.Length == 0)
            throw new AgentLlmException("The summary model returned an empty summary.");

        return summary;
    }
}

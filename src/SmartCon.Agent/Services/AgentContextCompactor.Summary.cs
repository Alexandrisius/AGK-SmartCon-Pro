using System.Text;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services;

public sealed partial class AgentContextCompactor
{
    private const int MaxSummaryDepth = 2;
    private const int MaxTranscriptCharsPerMessage = 4000;

    private async Task<string> SummarizeCompactionAsync(
        IReadOnlyList<AgentMessage> messages,
        AgentCompactionRequest request,
        CancellationToken ct)
    {
        var transcript = BuildTranscript(messages);
        var maxInput = Math.Max(4000, _summaryMaxInputTokens);
        if (AgentTokenEstimator.EstimateText(transcript) <= maxInput)
            return await SummarizeChunkAsync(transcript, ct).ConfigureAwait(false);

        return await SummarizeTextInChunksAsync(transcript, maxInput, depth: 0, ct)
            .ConfigureAwait(false);
    }

    private async Task<string> SummarizeTextInChunksAsync(
        string text,
        int maxInputTokens,
        int depth,
        CancellationToken ct)
    {
        if (AgentTokenEstimator.EstimateText(text) <= maxInputTokens || depth >= MaxSummaryDepth)
            return await SummarizeChunkAsync(text, ct).ConfigureAwait(false);

        var chunks = SplitText(text, maxInputTokens);
        var summaries = new List<string>(chunks.Count);
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            summaries.Add(await SummarizeChunkAsync(chunk, ct).ConfigureAwait(false));
        }

        var combined = string.Join("\n\n---\n\n", summaries);
        return AgentTokenEstimator.EstimateText(combined) <= maxInputTokens
            ? combined
            : await SummarizeTextInChunksAsync(combined, maxInputTokens, depth + 1, ct)
                .ConfigureAwait(false);
    }

    private Task<string> SummarizeChunkAsync(string text, CancellationToken ct) =>
        _summarizer.SummarizeAsync(text, _summaryMaxOutputTokens, ct);

    /// <summary>Splits a transcript on line boundaries into ~maxInputTokens chunks.</summary>
    private static List<string> SplitText(string text, int maxInputTokens)
    {
        var result = new List<string>();
        // Cyrillic is estimated at ~2.5 chars/token; 2 keeps every chunk under the cap.
        var approximateChars = Math.Max(1000, maxInputTokens * 2);
        var builder = new StringBuilder(approximateChars);
        foreach (var line in text.Split('\n'))
        {
            if (line.Length > approximateChars)
            {
                if (builder.Length > 0)
                {
                    result.Add(builder.ToString());
                    builder.Clear();
                }

                for (var offset = 0; offset < line.Length; offset += approximateChars)
                {
                    var length = Math.Min(approximateChars, line.Length - offset);
                    result.Add(line.Substring(offset, length));
                }

                continue;
            }

            if (builder.Length > 0 && builder.Length + line.Length + 1 > approximateChars)
            {
                result.Add(builder.ToString());
                builder.Clear();
            }

            builder.Append(line).Append('\n');
        }

        if (builder.Length > 0)
            result.Add(builder.ToString());

        return result;
    }

    private static string BuildTranscript(IReadOnlyList<AgentMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case AgentRole.User:
                    builder.Append("USER: ").AppendLine(Truncate(message.Content));
                    break;

                case AgentRole.Assistant:
                    builder.Append("ASSISTANT: ").AppendLine(Truncate(message.Content));
                    if (message.ToolCalls.Count > 0)
                    {
                        builder.Append("  calls: ")
                            .AppendLine(string.Join(", ", message.ToolCalls.Select(call =>
                                call.Name + "(" + Truncate(call.ArgumentsJson, 300) + ")")));
                    }

                    break;

                case AgentRole.Tool:
                    builder.Append("TOOL RESULT")
                        .Append(message.ToolCallId is null ? string.Empty : " [" + message.ToolCallId + "]")
                        .Append(": ")
                        .AppendLine(Truncate(message.Content));
                    break;

                case AgentRole.System:
                    builder.Append("SYSTEM: ").AppendLine(Truncate(message.Content));
                    break;
            }
        }

        return builder.ToString();
    }

    private static string Truncate(string? value, int limit = MaxTranscriptCharsPerMessage)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value!.Length <= limit ? value : value.Substring(0, limit) + "…";
    }
}

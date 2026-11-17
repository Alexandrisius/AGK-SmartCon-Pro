using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services;

public static class AgentTokenEstimator
{
    public const int ImageTokenEstimate = 1024;

    public static int Estimate(AgentMessage message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));

        var characters = message.Content?.Length ?? 0;
        characters += message.ReasoningContent?.Length ?? 0;
        foreach (var call in message.ToolCalls)
            characters += call.Name.Length + call.ArgumentsJson.Length;

        var divisor = ContainsCyrillic(message.Content) ? 2.5 : 3.5;
        var tokens = (int)Math.Ceiling(characters / divisor) + 4;
        tokens += message.Attachments.Count * ImageTokenEstimate;
        return tokens;
    }

    public static int Estimate(IReadOnlyList<AgentMessage> messages)
    {
        var total = 0;
        foreach (var message in messages)
            total += Estimate(message);
        return total;
    }

    private static bool ContainsCyrillic(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        var value = text!;
        var cyrillic = 0;
        foreach (var character in value)
        {
            if (character is >= '\u0400' and <= '\u04FF')
                cyrillic++;
        }

        return cyrillic > 0 && cyrillic * 3 >= value.Length;
    }
}
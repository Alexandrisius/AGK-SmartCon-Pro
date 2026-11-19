namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Session title policy: the first user message becomes the session title.
/// A generous cap keeps the title meaningful for wide panels; the UI trims it
/// to the actual available width instead of cutting it at a fixed 60 chars.
/// </summary>
public static class AgentConversationTitle
{
    public const int MaxLength = 200;

    public static string FromFirstMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var title = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return title.Length > MaxLength
            ? title.Remove(MaxLength) + "…"
            : title;
    }
}
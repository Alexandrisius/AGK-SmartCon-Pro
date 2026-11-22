namespace SmartCon.Agent.Services.Llm;

/// <summary>
/// Per-client memory of what the provider's API actually accepts. Initialized
/// from the settings and degraded one step per rejected request (HTTP 400).
/// Every change is remembered for the client lifetime so later requests do not
/// re-trigger the rejection. Thread-safe: one client can serve several
/// concurrent conversations.
/// </summary>
internal sealed class AgentProviderCapabilities
{
    private const int BudgetFloor = 4096;
    private const int MaxHalvings = 3;

    // Words that mark an error body as "the parameter value is not accepted".
    // Exemplary, not exhaustive: substring checks, never model names.
    private static readonly string[] ValidityWords =
    {
        "invalid", "illegal", "unsupported", "not supported", "not a valid",
        "must be", "expected", "valid value"
    };

    private readonly object _gate = new();

    public AgentProviderCapabilities(string effort, AgentReasoningFormat format)
    {
        Effort = effort;
        Format = format;
    }

    public string Effort { get; private set; }

    public AgentReasoningFormat Format { get; private set; }

    private int Halvings { get; set; }

    private bool OmitBudget { get; set; }

    public AgentReasoningOptions? ReasoningOptions()
    {
        lock (_gate)
        {
            return Format == AgentReasoningFormat.None
                ? null
                : new AgentReasoningOptions(Format, Effort);
        }
    }

    /// <summary>
    /// Effective <c>max_tokens</c> for a request: the requested budget with the
    /// remembered halvings applied, or "omit the field" after the provider kept
    /// rejecting it. Omit is true only when the request asked for a budget.
    /// </summary>
    public (int? Budget, bool Omit) EffectiveBudget(int? requested)
    {
        lock (_gate)
        {
            if (!requested.HasValue)
                return (null, false);
            if (OmitBudget)
                return (null, true);

            var value = requested.Value;
            for (var i = 0; i < Halvings; i++)
                value = Math.Max(value / 2, BudgetFloor);
            return (value, false);
        }
    }

    public (string Effort, AgentReasoningFormat Format) Snapshot()
    {
        lock (_gate)
        {
            return (Effort, Format);
        }
    }

    /// <summary>
    /// Classifies a lower-cased HTTP 400 error body and applies exactly one
    /// graceful degradation step. Returns the human-readable change for the
    /// log line, or null when the body is unrelated (the caller must fail).
    /// </summary>
    public string? TryDegrade(string errorBody, int? requestedBudget)
    {
        lock (_gate)
        {
            var body = (errorBody ?? string.Empty).ToLowerInvariant();

            if (Format != AgentReasoningFormat.None && MentionsEffortValue(body, Effort))
            {
                var next = NextEffort(Effort);
                if (next is not null)
                {
                    var previous = Effort;
                    Effort = next;
                    return ParameterName(Format) + "=" + previous +
                        "; continuing with " + ParameterName(Format) + "=" + next +
                        " (provider reported the effort value invalid)";
                }

                var oldFormat = Format;
                Format = AgentReasoningFormat.None;
                return ParameterName(oldFormat) + "=" + Effort +
                    "; continuing without the reasoning parameter" +
                    " (provider reported the effort value invalid)";
            }

            if (Format != AgentReasoningFormat.None && MentionsReasoningParameter(body))
            {
                var oldFormat = Format;
                Format = AgentReasoningFormat.None;
                return ParameterName(oldFormat) + "=" + Effort +
                    "; continuing without the reasoning parameter" +
                    " (provider does not accept the reasoning parameter)";
            }

            if (body.IndexOf("max_tokens", StringComparison.Ordinal) >= 0
                || body.IndexOf("max_completion_tokens", StringComparison.Ordinal) >= 0)
            {
                if (Halvings < MaxHalvings)
                {
                    Halvings++;
                    var effective = EffectiveBudget(requestedBudget).Budget;
                    return "max_tokens=" + BudgetText(requestedBudget) +
                        "; continuing with " + (effective.HasValue
                            ? "max_tokens=" + effective.Value
                            : "a halved output budget") +
                        " (provider reported the token limit invalid)";
                }

                if (!OmitBudget)
                {
                    OmitBudget = true;
                    return "max_tokens=" + BudgetText(requestedBudget) +
                        "; continuing without max_tokens" +
                        " (provider kept rejecting the token limit)";
                }

                return null;
            }

            return null;
        }
    }

    private string BudgetText(int? requested) => requested.HasValue
        ? requested.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "(unset)";

    private static string? NextEffort(string effort)
    {
        var ladder = AgentLlmSettings.ReasoningEffortLadder;
        var index = Array.IndexOf(ladder, effort);
        return index >= 0 && index < ladder.Length - 1 ? ladder[index + 1] : null;
    }

    private static string ParameterName(AgentReasoningFormat format) => format switch
    {
        AgentReasoningFormat.ReasoningEffort => "reasoning_effort",
        AgentReasoningFormat.ReasoningObject => "reasoning.effort",
        _ => "reasoning"
    };

    private static bool MentionsEffortValue(string body, string effort)
    {
        if (string.IsNullOrEmpty(effort))
            return false;
        if (!ContainsAny(body, ValidityWords))
            return false;

        return ContainsToken(body, effort);
    }

    private static bool MentionsReasoningParameter(string body) =>
        body.IndexOf("reasoning_effort", StringComparison.Ordinal) >= 0
        || body.IndexOf("reasoning", StringComparison.Ordinal) >= 0
        || body.IndexOf("thinking", StringComparison.Ordinal) >= 0
        || body.IndexOf("effort", StringComparison.Ordinal) >= 0;

    private static bool ContainsAny(string body, string[] words)
    {
        foreach (var word in words)
        {
            if (body.IndexOf(word, StringComparison.Ordinal) >= 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// "<c>max</c>" as a token: delimited by characters that are not letters,
    /// digits or underscores, so <c>max_tokens</c> does not match the effort
    /// value <c>max</c>, while <c>"max"</c>, <c>'max'</c> and <c> max </c> do.
    /// </summary>
    private static bool ContainsToken(string body, string token)
    {
        var index = body.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index == 0 ? ' ' : body[index - 1];
            var afterIndex = index + token.Length;
            var after = afterIndex >= body.Length ? ' ' : body[afterIndex];
            if (!IsWordChar(before) && !IsWordChar(after))
                return true;

            index = body.IndexOf(token, afterIndex, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsWordChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
        || (c >= '0' && c <= '9') || c == '_';
}

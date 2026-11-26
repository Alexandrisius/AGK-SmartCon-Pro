namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Question budget of one classify_types call: the decision channel of the
/// session fb3b66f6 died on 416 questions packed into one tool call - every
/// type size carried its own prompt - while narrow calls answered in seconds.
/// The run is therefore chunked by ESTIMATED questions (1 per family + 1 per
/// type), and the budget itself is a per-model setting (S.49, #534): a free
/// decision model drops long request chains, a big one can take more.
/// </summary>
public static class AgentClassificationBudget
{
    /// <summary>Conservative default: safe for free/small decision models.</summary>
    public const int DefaultMaxQuestionsPerToolCall = 40;

    /// <summary>Clamp of the per-model setting (env SMARTCON_DECIDER_MAX_QUESTIONS).</summary>
    public const int MinQuestionsPerToolCall = 10;

    public const int MaxQuestionsPerToolCall = 2000;

    public static int Normalize(int? value) =>
        value is null ? DefaultMaxQuestionsPerToolCall : Normalize(value.Value);

    public static int Normalize(int value)
    {
        if (value < MinQuestionsPerToolCall)
            return MinQuestionsPerToolCall;
        return value > MaxQuestionsPerToolCall ? MaxQuestionsPerToolCall : value;
    }

    /// <summary>
    /// Worst-case question count of one family: the family question plus one
    /// per type when its codes need per-type disambiguation (the shape that
    /// produced 416 questions over ~30 families).
    /// </summary>
    public static int EstimateQuestions(ClassifyFamilyGroup group) => 1 + group.Types.Count;

    /// <summary>Estimate over the whole selection, before any chunking.</summary>
    public static int EstimateQuestions(IReadOnlyList<ClassifyFamilyGroup> groups)
    {
        var total = 0;
        foreach (var group in groups)
            total += EstimateQuestions(group);
        return total;
    }

    /// <summary>
    /// Splits the families into chunks that each stay within the question
    /// budget, keeping the input order. A single family whose own estimate
    /// exceeds the budget becomes a chunk of its own (never dropped and never
    /// deferred forever - the caller reports it as oversized); everything else
    /// fits.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<ClassifyFamilyGroup>> ChunkByQuestionBudget(
        IReadOnlyList<ClassifyFamilyGroup> groups,
        int maxQuestions,
        out List<ClassifyFamilyGroup> oversized)
    {
        oversized = new List<ClassifyFamilyGroup>();
        var chunks = new List<IReadOnlyList<ClassifyFamilyGroup>>();
        var current = new List<ClassifyFamilyGroup>();
        var used = 0;
        foreach (var group in groups)
        {
            var estimate = EstimateQuestions(group);
            if (estimate > maxQuestions)
            {
                // Oversized alone: process it, but name it so the caller can
                // suggest narrowing (typeIds) instead of looping.
                oversized.Add(group);
                if (current.Count > 0)
                {
                    chunks.Add(current);
                    current = new List<ClassifyFamilyGroup>();
                    used = 0;
                }

                chunks.Add(new[] { group });
                continue;
            }

            if (used + estimate > maxQuestions && current.Count > 0)
            {
                chunks.Add(current);
                current = new List<ClassifyFamilyGroup>();
                used = 0;
            }

            current.Add(group);
            used += estimate;
        }

        if (current.Count > 0)
            chunks.Add(current);

        return chunks;
    }
}

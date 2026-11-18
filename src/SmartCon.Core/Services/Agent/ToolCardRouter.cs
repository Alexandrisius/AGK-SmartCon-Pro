using System.Text;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// BM25 ranking over the enriched tool cards (runbook H1-08). The positive
/// document contains name (boosted), purpose, when-to-use, examples and
/// synonyms; negative examples are scored separately and subtracted, so a
/// query that matches "when NOT to use" wording pushes the tool down.
/// </summary>
public static class ToolCardRouter
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const int NameBoost = 3;
    private const double NegativeWeight = 0.4;

    /// <summary>
    /// Cards with a positive score above zero, ordered by score descending and
    /// then by tool name. Tools without card text never match.
    /// </summary>
    public static IReadOnlyList<AgentToolDefinition> Rank(
        string query,
        IReadOnlyList<AgentToolDefinition> tools,
        int max)
    {
        if (tools is null || tools.Count == 0 || max <= 0)
            return Array.Empty<AgentToolDefinition>();

        var queryTerms = RuleToolRouter.Tokenize(query ?? string.Empty)
            .Distinct()
            .ToList();
        if (queryTerms.Count == 0)
            return Array.Empty<AgentToolDefinition>();

        var positive = new Document[tools.Count];
        var negative = new Document[tools.Count];
        for (var index = 0; index < tools.Count; index++)
        {
            positive[index] = CreateDocument(PositiveText(tools[index]));
            negative[index] = CreateDocument(string.Join(" ", tools[index].NegativeExamples));
        }

        var positiveScores = Score(positive, queryTerms);
        var negativeScores = Score(negative, queryTerms);

        return tools
            .Select((tool, index) => (
                Tool: tool,
                Score: System.Math.Max(
                    0.0,
                    positiveScores[index] - NegativeWeight * negativeScores[index])))
            .Where(pair => pair.Score > 0)
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.Tool.Name, StringComparer.Ordinal)
            .Take(System.Math.Min(max, tools.Count))
            .Select(pair => pair.Tool)
            .ToList();
    }

    /// <summary>Top candidates for find_tools and the decision reranker.</summary>
    public static IReadOnlyList<AgentToolDefinition> TopCandidates(
        string query,
        IReadOnlyList<AgentToolDefinition> tools,
        int max = 50) =>
        Rank(query, tools, max);

    private static string PositiveText(AgentToolDefinition tool)
    {
        var builder = new StringBuilder();
        for (var repeat = 0; repeat < NameBoost; repeat++)
            builder.Append(tool.Name).Append(' ');

        builder.Append(tool.Purpose).Append(' ');
        builder.Append(tool.WhenToUse);

        AppendList(builder, tool.Examples);
        AppendList(builder, tool.Synonyms);
        return builder.ToString();
    }

    private static void AppendList(StringBuilder builder, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return;

        builder.Append(' ');
        builder.Append(string.Join(" ", values));
    }

    private static double[] Score(Document[] documents, IReadOnlyList<string> queryTerms)
    {
        var scores = new double[documents.Length];
        if (documents.Length == 0)
            return scores;

        var averageLength = documents.Average(document => document.Length);
        if (averageLength <= 0)
            return scores;

        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in queryTerms)
            documentFrequency[term] = documents.Count(document => document.Terms.ContainsKey(term));

        for (var index = 0; index < documents.Length; index++)
        {
            var document = documents[index];
            var score = 0.0;
            foreach (var term in queryTerms)
            {
                if (!document.Terms.TryGetValue(term, out var frequency) || frequency == 0)
                    continue;

                var df = documentFrequency[term];
                var idf = System.Math.Log(
                    1 + (documents.Length - df + 0.5) / (df + 0.5));
                score += idf * frequency * (K1 + 1) /
                         (frequency + K1 * (1 - B + B * document.Length / averageLength));
            }

            scores[index] = score;
        }

        return scores;
    }

    private static Document CreateDocument(string text)
    {
        var terms = RuleToolRouter.Tokenize(text ?? string.Empty);
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in terms)
            frequencies[term] = frequencies.TryGetValue(term, out var count) ? count + 1 : 1;

        return new Document(terms.Count, frequencies);
    }

    private readonly struct Document
    {
        public Document(int length, Dictionary<string, int> terms)
        {
            Length = length;
            Terms = terms;
        }

        public int Length { get; }

        public Dictionary<string, int> Terms { get; }
    }
}

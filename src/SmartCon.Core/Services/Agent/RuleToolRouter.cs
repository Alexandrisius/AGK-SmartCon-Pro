using System.Globalization;
using System.Text;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>Result of tool routing for one user request.</summary>
public sealed class ToolRoutingDecision
{
    public required IReadOnlyList<AgentToolDefinition> Tools { get; init; }

    /// <summary>True when routing had no signal and the full catalog was returned.</summary>
    public bool Fallback { get; init; }

    public double TopScore { get; init; }
}

/// <summary>
/// Narrows the tool catalog for one request (mvp-plan §20.4). Implementations
/// must never invent tools: the decision contains definitions from the input
/// list only.
/// </summary>
public interface IToolRouter
{
    Task<ToolRoutingDecision> RouteAsync(
        string query,
        IReadOnlyList<AgentToolDefinition> tools,
        int maxTools = 14,
        CancellationToken ct = default);
}

/// <summary>
/// Deterministic BM25 shortlist over name + description + tags. Core read tools
/// are always included so the model can always look around; the remaining slots
/// go to the best-scoring write tools. Zero signal → the full catalog with
/// Fallback=true (fail-open: a bad shortlist is worse than a few extra schemas).
/// </summary>
public sealed class RuleToolRouter : IToolRouter
{
    private static readonly HashSet<string> CoreTools = new(StringComparer.Ordinal)
    {
        "get_project_overview",
        "list_views",
        "list_levels",
        "query_elements",
        "get_element_details",
        "summarize_elements",
        "find_tools",
        "execute_plan",
        "report_tooling_feedback"
    };

    private static readonly string[] RussianSuffixes =
    {
        "ения", "ении", "ение", "овать", "евать", "ация", "ации", "ацию",
        "ать", "ять", "ить", "еть", "уть", "ыть", "ешь", "ишь", "ует", "уют",
        "уй", "ай",
        "ый", "ий", "ая", "ое", "ые", "ых", "ым", "ими", "ому", "его", "ого",
        "ам", "ах", "ов", "ев", "ей", "ой", "ую", "юю", "ия", "ии", "ию",
        "ть", "ит", "ет", "ут", "ат", "ят", "ил", "ла", "ло", "ли",
        "и", "ы", "а", "о", "е", "у", "ю", "я", "ь"
    };

    private const double K1 = 1.2;
    private const double B = 0.75;
    private const int NameBoost = 3;

    /// <summary>Tools that every routed request keeps (read-around + catalog search).</summary>
    public static IReadOnlyCollection<string> CoreToolNames => CoreTools;

    public Task<ToolRoutingDecision> RouteAsync(
        string query,
        IReadOnlyList<AgentToolDefinition> tools,
        int maxTools = 14,
        CancellationToken ct = default) =>
        Task.FromResult(RouteCore(query, tools, maxTools, includeCore: true));

    /// <summary>
    /// Pure BM25 ranking without the always-on core-read tools. Used by
    /// <c>find_tools</c>: its small limit must be spent on matches, not on the
    /// read tools the model already has (live dogfood P0-3).
    /// </summary>
    public static ToolRoutingDecision RankOnly(
        string query,
        IReadOnlyList<AgentToolDefinition> tools,
        int maxTools) =>
        RouteCore(query, tools, maxTools, includeCore: false);

    private static ToolRoutingDecision RouteCore(
        string query,
        IReadOnlyList<AgentToolDefinition> tools,
        int maxTools,
        bool includeCore)
    {
        if (tools.Count == 0)
            return new ToolRoutingDecision { Tools = Array.Empty<AgentToolDefinition>() };

        var safeMax = System.Math.Max(1, System.Math.Min(maxTools, tools.Count));
        var queryTerms = Tokenize(query ?? string.Empty).Distinct().ToList();
        if (queryTerms.Count == 0)
            return new ToolRoutingDecision { Tools = tools, Fallback = true };

        var documents = tools.Select(CreateDocument).ToList();
        var averageLength = documents.Average(document => document.Length);
        if (averageLength <= 0)
            return new ToolRoutingDecision { Tools = tools, Fallback = true };

        var scores = new double[documents.Count];
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in queryTerms)
        {
            var df = documents.Count(document => document.Terms.ContainsKey(term));
            documentFrequency[term] = df;
        }

        for (var index = 0; index < documents.Count; index++)
        {
            var document = documents[index];
            var score = 0.0;
            foreach (var term in queryTerms)
            {
                if (!document.Terms.TryGetValue(term, out var tf) || tf == 0)
                    continue;

                var df = documentFrequency[term];
                var idf = System.Math.Log(1 + (documents.Count - df + 0.5) / (df + 0.5));
                score += idf * tf * (K1 + 1) /
                         (tf + K1 * (1 - B + B * document.Length / averageLength));
            }

            scores[index] = score;
        }

        var topScore = scores.Length == 0 ? 0 : scores.Max();
        if (topScore <= 0)
            return new ToolRoutingDecision { Tools = tools, Fallback = true };

        var selected = new List<AgentToolDefinition>(safeMax);
        var selectedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            if (!includeCore || !CoreTools.Contains(tool.Name) || !selectedNames.Add(tool.Name))
                continue;

            selected.Add(tool);
            if (selected.Count >= safeMax)
                return new ToolRoutingDecision { Tools = selected, TopScore = topScore };
        }

        var ranked = tools
            .Select((tool, index) => (Tool: tool, Score: scores[index]))
            .Where(pair => !selectedNames.Contains(pair.Tool.Name) && pair.Score > 0)
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.Tool.Name, StringComparer.Ordinal);

        foreach (var (tool, _) in ranked)
        {
            if (selected.Count >= safeMax)
                break;

            if (selectedNames.Add(tool.Name))
                selected.Add(tool);
        }

        return new ToolRoutingDecision { Tools = selected, TopScore = topScore };
    }

    private static Document CreateDocument(AgentToolDefinition tool)
    {
        var builder = new StringBuilder();
        for (var repeat = 0; repeat < NameBoost; repeat++)
            builder.Append(tool.Name).Append(' ');
        builder.Append(tool.Description);
        if (tool.Tags.Count > 0)
        {
            builder.Append(' ');
            builder.Append(string.Join(" ", tool.Tags));
        }

        var terms = Tokenize(builder.ToString());
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in terms)
            frequencies[term] = frequencies.TryGetValue(term, out var count) ? count + 1 : 1;

        return new Document(terms.Count, frequencies);
    }

    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            Flush();
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (builder.Length == 0)
                return;

            var token = builder.ToString();
            builder.Clear();
            if (token.Length < 2)
                return;

            tokens.Add(Stem(token));
        }
    }

    private static string Stem(string token)
    {
        if (token.Length <= 4)
            return token;

        var hasCyrillic = false;
        foreach (var ch in token)
        {
            if (ch < '\u0400' || ch > '\u04FF')
                continue;

            hasCyrillic = true;
            break;
        }

        if (!hasCyrillic)
        {
            if (token.EndsWith("ies", StringComparison.Ordinal) && token.Length > 5)
                return token.Substring(0, token.Length - 3) + "y";
            if (token.EndsWith("es", StringComparison.Ordinal) && token.Length > 5)
                return token.Substring(0, token.Length - 2);
            if (token.EndsWith("s", StringComparison.Ordinal) && token.Length > 4)
                return token.Substring(0, token.Length - 1);
            return token;
        }

        foreach (var suffix in RussianSuffixes)
        {
            if (token.Length - suffix.Length < 3)
                continue;

            if (token.EndsWith(suffix, StringComparison.Ordinal))
                return token.Substring(0, token.Length - suffix.Length);
        }

        return token;
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

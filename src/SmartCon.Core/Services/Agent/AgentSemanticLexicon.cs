using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// The deterministic half of semantic matching: tokenization and the lexical
/// prefilter that narrows a big candidate list before any model call.
/// </summary>
internal static class AgentSemanticLexicon
{
    private const int MinPrefixLength = 5;

    /// <summary>
    /// Scores every leaf by the tokens it shares with the family text. Two
    /// tokens match when equal, or one is a prefix of the other while the
    /// shorter is at least 5 chars. Family-name tokens weigh 2, the rest 1. The
    /// best <paramref name="take"/> leaves with a score above zero survive,
    /// score first then code order.
    /// </summary>
    internal static IReadOnlyList<AgentClassificationEntry> LexicalTop(
        IReadOnlyList<string> nameTokens,
        IReadOnlyList<string> otherTokens,
        IReadOnlyList<AgentClassificationEntry> leaves,
        Dictionary<string, HashSet<string>> leafTokens,
        int take)
    {
        var weighted = nameTokens
            .Select(token => (Token: token, Weight: 2))
            .Concat(otherTokens
                .Where(token => !nameTokens.Contains(token))
                .Select(token => (Token: token, Weight: 1)))
            .ToList();
        if (weighted.Count == 0)
            return Array.Empty<AgentClassificationEntry>();

        var scored = new List<(AgentClassificationEntry Leaf, int Score)>();
        foreach (var leaf in leaves)
        {
            if (!leafTokens.TryGetValue(leaf.Code, out var tokens))
                continue;

            var score = weighted.Sum(pair =>
                tokens.Any(leafToken => TokensMatch(pair.Token, leafToken)) ? pair.Weight : 0);
            if (score > 0)
                scored.Add((leaf, score));
        }

        return scored
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.Leaf.Code, AgentClassificationTable.CodeComparer)
            .Take(take)
            .Select(pair => pair.Leaf)
            .ToArray();
    }

    internal static bool TokensMatch(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
            return true;

        if (left.Length < MinPrefixLength || right.Length < MinPrefixLength)
            return false;

        return left.Length < right.Length
            ? right.StartsWith(left, StringComparison.Ordinal)
            : left.StartsWith(right, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lower-case tokens, ё folded to е, split on every non-letter/digit. A pure
    /// number is dropped: a diameter or a size says nothing about what the
    /// element is, and matching on it would turn a weak family into a strong
    /// one by accident.
    /// </summary>
    internal static string[] Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();

        var tokens = new List<string>();
        var builder = new System.Text.StringBuilder();
        foreach (var character in text!)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                continue;
            }

            Flush(builder, tokens);
        }

        Flush(builder, tokens);
        return tokens.ToArray();
    }

    private static void Flush(System.Text.StringBuilder builder, List<string> tokens)
    {
        if (builder.Length == 0)
            return;

        var token = builder.ToString().ToLowerInvariant().Replace('ё', 'е');
        if (!token.All(character => char.IsDigit(character)) && !tokens.Contains(token))
            tokens.Add(token);
        builder.Clear();
    }
}

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// "Did you mean" suggestions for a family name that matched nothing: the closest
/// loaded names by edit distance and by shared words (a typo or another word order
/// must not cost a round of list_families).
/// </summary>
internal static class AgentNameSuggestions
{
    /// <summary>
    /// Up to <paramref name="max"/> candidates that are close to
    /// <paramref name="query"/>, best first; empty when nothing is close.
    /// </summary>
    internal static IReadOnlyList<string> Nearest(
        string query,
        IEnumerable<string> candidates,
        int max = 3)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<string>();
        }

        var wanted = query.Trim();
        var scored = new List<(string Name, double Score)>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var name = candidate.Trim();
            var length = Math.Max(wanted.Length, name.Length);
            if (length == 0)
            {
                continue;
            }

            var byDistance = 1.0 - (double)EditDistance(wanted, name) / length;
            var byWords = WordOverlap(wanted, name);
            var score = Math.Max(byDistance, byWords);
            if (score >= 0.5)
            {
                scored.Add((name, score));
            }
        }

        return scored
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Name)
            .Take(Math.Max(0, max))
            .ToList();
    }

    /// <summary>
    /// The standard suffix of a "no such family" message: nothing when no loaded
    /// name is close.
    /// </summary>
    internal static string Suffix(string query, IEnumerable<string> candidates) =>
        Nearest(query, candidates) is { Count: > 0 } nearest
            ? " Nearest loaded name(s): " + string.Join(", ", nearest) + "."
            : string.Empty;

    private static double WordOverlap(string left, string right)
    {
        var leftWords = Split(left);
        var rightWords = Split(right);
        if (leftWords.Count == 0 || rightWords.Count == 0)
        {
            return 0;
        }

        var common = leftWords.Count(word => rightWords.Contains(word));
        return (double)common / Math.Max(leftWords.Count, rightWords.Count);
    }

    private static HashSet<string> Split(string text) => new(
        text.Split(new[] { ' ', '_', '-', '.', ',' }, StringSplitOptions.RemoveEmptyEntries),
        StringComparer.OrdinalIgnoreCase);

    private static int EditDistance(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = previous[column - 1] + (char.ToLowerInvariant(left[row - 1]) == char.ToLowerInvariant(right[column - 1]) ? 0 : 1);
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}

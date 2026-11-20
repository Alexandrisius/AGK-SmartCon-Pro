using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools;

/// <summary>
/// Resolves category names coming from the model to Revit categories. Accepts
/// the BuiltInCategory enum name (OST_Walls) and the localized display name,
/// which is what users type in Russian.
/// </summary>
internal static class AgentCategoryResolver
{
    internal static IReadOnlyList<ElementId> Resolve(
        Document doc,
        IReadOnlyList<string> names,
        ICollection<string> unknown)
    {
        var ids = new List<ElementId>(names.Count);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (Enum.TryParse<BuiltInCategory>(name, ignoreCase: true, out var builtIn))
            {
                var byBuiltIn = RevitCategoryLookup(doc, builtIn);
                if (byBuiltIn is not null)
                {
                    AddOnce(ids, byBuiltIn.Id);
                    continue;
                }
            }

            Category? match = null;
            foreach (Category category in doc.Settings.Categories)
            {
                if (string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    match = category;
                    break;
                }
            }

            if (match is null)
            {
                unknown.Add(name);
                continue;
            }

            AddOnce(ids, match.Id);
        }

        return ids;
    }

    private static Category? RevitCategoryLookup(Document doc, BuiltInCategory builtIn)
    {
        try
        {
            return Category.GetCategory(doc, builtIn);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Closest category names for an unknown_category error: the model often
    /// guesses a plural OST_ name (OST_PipeFittings vs OST_PipeFitting), and a
    /// suggestion closes the loop in one retry (external harness feedback).
    /// </summary>
    internal static string SuggestSuffix(Document doc, string name)
    {
        var suggestions = SuggestClosest(doc, name);
        return suggestions.Count == 0
            ? string.Empty
            : "; closest: " + string.Join(", ", suggestions);
    }

    internal static IReadOnlyList<string> SuggestClosest(Document doc, string name, int max = 3)
    {
        var target = name.Trim();
        if (target.Length == 0)
            return Array.Empty<string>();

        var candidates = new List<string>();
        foreach (Category category in doc.Settings.Categories)
        {
            if (!string.IsNullOrEmpty(category.Name))
                candidates.Add(category.Name!);
        }

        foreach (BuiltInCategory value in Enum.GetValues(typeof(BuiltInCategory)))
        {
            var text = value.ToString();
            if (!candidates.Contains(text, StringComparer.OrdinalIgnoreCase))
                candidates.Add(text);
        }

        var scored = new List<KeyValuePair<string, int>>();
        foreach (var candidate in candidates)
        {
            var score = SimilarityScore(target, candidate);
            if (score <= 2)
                scored.Add(new KeyValuePair<string, int>(candidate, score));
        }

        return scored
            .OrderBy(pair => pair.Value)
            .ThenBy(pair => pair.Key.Length)
            .Take(max)
            .Select(pair => pair.Key)
            .ToList();
    }

    private static int SimilarityScore(string target, string candidate)
    {
        var left = target.ToLowerInvariant();
        var right = candidate.ToLowerInvariant();
        if (left == right)
            return 0;
        if (right.StartsWith(left, StringComparison.Ordinal)
            || left.StartsWith(right, StringComparison.Ordinal))
        {
            return 1;
        }

        if (right.IndexOf(left, StringComparison.Ordinal) >= 0
            || left.IndexOf(right, StringComparison.Ordinal) >= 0)
        {
            return 2;
        }

        var distance = EditDistance(left, right);
        return distance <= 1 ? distance : int.MaxValue;
    }

    private static int EditDistance(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1)
            return int.MaxValue;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static void AddOnce(ICollection<ElementId> ids, ElementId id)
    {
        foreach (var existing in ids)
        {
            if (existing.GetValue() == id.GetValue())
                return;
        }

        ids.Add(id);
    }
}

internal static class AgentPaging
{
    internal const int DefaultLimit = 200;

    internal const int MaxLimit = 500;

    internal static int NormalizeOffset(int offset) => Math.Max(0, offset);

    internal static int NormalizeLimit(int limit, int fallback = DefaultLimit, int max = MaxLimit)
    {
        var value = limit <= 0 ? fallback : limit;
        return Math.Max(1, Math.Min(value, max));
    }

    internal static IReadOnlyList<T> Slice<T>(IReadOnlyList<T> items, int offset, int limit)
    {
        if (offset >= items.Count)
            return Array.Empty<T>();

        var count = Math.Min(limit, items.Count - offset);
        var result = new List<T>(count);
        for (var index = 0; index < count; index++)
            result.Add(items[offset + index]);
        return result;
    }
}
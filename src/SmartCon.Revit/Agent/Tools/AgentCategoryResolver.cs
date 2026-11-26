using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools;

/// <summary>
/// Resolves category names coming from the model to Revit categories. Accepts
/// the BuiltInCategory enum name (OST_Walls), the localized display name,
/// which is what users type in Russian, and enum-shaped aliases of display
/// names (OST_Pipes for the category displayed as "Pipes", OST_PipeCurves).
/// </summary>
internal static class AgentCategoryResolver
{
    private const string EnumPrefix = "OST_";

    internal static IReadOnlyList<ElementId> Resolve(
        Document doc,
        IReadOnlyList<string> names,
        ICollection<string> unknown)
    {
        var ids = new List<ElementId>(names.Count);
        List<Category>? displayNames = null;
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            // A category id is accepted too (list_categories returns it as categoryId).
            if (long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawId))
            {
                var byId = Category.GetCategory(doc, ElementIdCompat.Create(rawId));
                if (byId is not null)
                {
                    AddOnce(ids, byId.Id);
                    continue;
                }

                unknown.Add(name);
                continue;
            }

            if (Enum.TryParse<BuiltInCategory>(name, ignoreCase: true, out var builtIn))
            {
                var byBuiltIn = RevitCategoryLookup(doc, builtIn);
                if (byBuiltIn is not null)
                {
                    AddOnce(ids, byBuiltIn.Id);
                    continue;
                }
            }

            displayNames ??= CollectDisplayNames(doc);
            Category? match = FindByDisplayName(displayNames, name);
            if (match is not null)
            {
                AddOnce(ids, match.Id);
                continue;
            }

            // OST_-shaped guesses that are not enum members: OST_Pipes means the
            // category displayed as "Pipes" (= OST_PipeCurves). One match is
            // accepted; several are reported back instead of a silent pick.
            var alias = MatchAliasNames(name, displayNames.Select(category => category.Name!).ToList());
            if (alias.Kind == AliasMatchKind.Unique)
            {
                var aliased = FindByDisplayName(displayNames, alias.Matches[0]);
                if (aliased is not null)
                {
                    AddOnce(ids, aliased.Id);
                    continue;
                }
            }

            unknown.Add(
                alias.Kind == AliasMatchKind.Ambiguous
                    ? name + " (ambiguous — matches: " + string.Join(", ", alias.Matches) + ")"
                    : name);
        }

        return ids;
    }

    /// <summary>
    /// Display-name match of a category in the document, case-insensitive.
    /// </summary>
    private static Category? FindByDisplayName(List<Category> categories, string name)
    {
        foreach (var category in categories)
        {
            if (string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase))
                return category;
        }

        return null;
    }

    /// <summary>
    /// All categories of the document that carry a non-empty display name.
    /// </summary>
    private static List<Category> CollectDisplayNames(Document doc)
    {
        var categories = new List<Category>();
        foreach (Category category in doc.Settings.Categories)
        {
            if (!string.IsNullOrEmpty(category.Name))
                categories.Add(category);
        }

        return categories;
    }

    internal enum AliasMatchKind
    {
        None = 0,
        Unique = 1,
        Ambiguous = 2
    }

    internal readonly struct AliasNameMatch
    {
        internal AliasNameMatch(AliasMatchKind kind, IReadOnlyList<string> matches)
        {
            Kind = kind;
            Matches = matches;
        }

        internal AliasMatchKind Kind { get; }

        internal IReadOnlyList<string> Matches { get; }
    }

    /// <summary>
    /// Alias matching for enum-shaped names that are not BuiltInCategory members
    /// (live case 6 #C6-06): the name with the OST_ prefix, underscores and
    /// whitespace stripped is compared to the display names the same way —
    /// "OST_Pipes" / "Pipes" both mean the category displayed as "Pipes"
    /// (OST_PipeCurves). Exactly one normalized match is accepted; more than one
    /// is ambiguous and must be reported, not silently picked. Pure string logic,
    /// pinned by unit tests; the document side is pinned by integration tests.
    /// </summary>
    internal static AliasNameMatch MatchAliasNames(string name, IReadOnlyList<string> displayNames)
    {
        var suffix = NormalizeAlias(name);
        if (suffix.Length == 0)
            return new AliasNameMatch(AliasMatchKind.None, Array.Empty<string>());

        List<string>? matches = null;
        foreach (var displayName in displayNames)
        {
            if (string.Equals(displayName, name, StringComparison.OrdinalIgnoreCase)
                || NormalizeAlias(displayName) != suffix)
            {
                continue;
            }

            matches ??= new List<string>();
            if (!matches.Contains(displayName, StringComparer.OrdinalIgnoreCase))
                matches.Add(displayName);
        }

        return matches switch
        {
            null => new AliasNameMatch(AliasMatchKind.None, Array.Empty<string>()),
            { Count: 1 } => new AliasNameMatch(AliasMatchKind.Unique, matches),
            _ => new AliasNameMatch(AliasMatchKind.Ambiguous, matches)
        };
    }

    /// <summary>
    /// "OST_Pipe_Curves" -> "pipecurves": lower-cased, OST_ prefix and every
    /// underscore/whitespace removed, so an enum-shaped guess compares to a
    /// display name with spaces.
    /// </summary>
    private static string NormalizeAlias(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.StartsWith(EnumPrefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(EnumPrefix.Length);

        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var character in trimmed.ToLowerInvariant())
        {
            if (character is not ('_' or ' ' or '\t'))
                builder.Append(character);
        }

        return builder.ToString();
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
    /// Every suggestion is a token this resolver accepts: a display name of the
    /// document, or an enum name whose category exists in the document.
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
        foreach (var category in CollectDisplayNames(doc))
        {
            candidates.Add(category.Name!);
            var id = category.Id.GetValue();
            if (id is < int.MinValue or > int.MaxValue)
                continue;

            // Only enum names the resolver actually resolves to: the enum has
            // hundreds of members with no category in a given document, and a
            // suggestion the caller cannot use costs a wasted round (C10-04).
            var builtIn = (BuiltInCategory)(int)id;
            if (!Enum.IsDefined(typeof(BuiltInCategory), builtIn))
                continue;

            var enumName = builtIn.ToString();
            if (!candidates.Contains(enumName, StringComparer.OrdinalIgnoreCase))
                candidates.Add(enumName);
        }

        var scored = new List<KeyValuePair<string, int>>();
        foreach (var candidate in candidates)
        {
            var score = SimilarityScore(target, candidate);
            if (score <= 3)
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

        // Same-family enum guesses (OST_PipeValveAssembly among the OST_Pipe*
        // categories): a shared "ost_"-prefixed normalized prefix of 8+ chars
        // says "same family", the shortest members of it first.
        var leftKey = "ost_" + NormalizeAlias(target);
        var rightKey = "ost_" + NormalizeAlias(candidate);
        if (SharedPrefixLength(leftKey, rightKey) >= 8)
        {
            return 3;
        }

        var distance = EditDistance(left, right);
        return distance <= 1 ? distance : int.MaxValue;
    }

    private static int SharedPrefixLength(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < length && left[index] == right[index])
            index++;

        return index;
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

using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// A classification table as a tree: source rows enriched with a breadcrumb and
/// a leaf flag, addressable by code. Pure logic — the Revit side only supplies
/// the rows and the display path of the file they were loaded from.
/// </summary>
public sealed class AgentClassificationTable
{
    public const string BreadcrumbSeparator = " › ";

    private readonly Dictionary<string, AgentClassificationEntry> _byCode;

    private AgentClassificationTable(
        string? source,
        List<AgentClassificationEntry> entries,
        Dictionary<string, AgentClassificationEntry> byCode)
    {
        Source = source;
        Entries = entries;
        _byCode = byCode;
    }

    /// <summary>A document without a loaded classification table.</summary>
    public static AgentClassificationTable Empty { get; } =
        Build(Array.Empty<AgentClassificationEntry>(), null);

    /// <summary>Compares codes segment by segment: «12» &lt; «12.34» &lt; «12.34.56» &lt; «13.01».</summary>
    public static IComparer<string> CodeComparer { get; } = new CodeComparison();

    /// <summary>Display path of the file the table was loaded from; null when none is assigned.</summary>
    public string? Source { get; }

    /// <summary>Rows in tree order: a parent always precedes its children.</summary>
    public IReadOnlyList<AgentClassificationEntry> Entries { get; }

    /// <summary>True when the table has at least one row.</summary>
    public bool Loaded => Entries.Count > 0;

    /// <summary>
    /// Builds the tree from flat rows. The first row of a duplicated code wins;
    /// a parent code the table does not contain makes the row a root; a parent
    /// cycle cannot loop the walk.
    /// </summary>
    public static AgentClassificationTable Build(
        IReadOnlyList<AgentClassificationEntry> rows,
        string? source)
    {
        var byCode = new Dictionary<string, AgentClassificationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var code = (row.Code ?? string.Empty).Trim();
            if (code.Length == 0)
                continue;

            var parent = string.IsNullOrWhiteSpace(row.ParentCode) ? null : row.ParentCode!.Trim();
            if (!byCode.ContainsKey(code))
            {
                byCode[code] = row with
                {
                    Code = code,
                    ParentCode = string.Equals(parent, code, StringComparison.OrdinalIgnoreCase)
                        ? null
                        : parent
                };
            }
        }

        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in byCode.Keys.ToList())
        {
            var entry = byCode[key];
            if (entry.ParentCode is not null && !byCode.ContainsKey(entry.ParentCode))
            {
                entry = entry with { ParentCode = null };
                byCode[key] = entry;
            }

            if (entry.ParentCode is not null)
                parents.Add(entry.ParentCode);
        }

        var breadcrumbs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<string, List<AgentClassificationEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in byCode.Keys.ToList())
        {
            var entry = byCode[key];
            entry = entry with
            {
                Breadcrumb = BreadcrumbOf(entry.Code, byCode, breadcrumbs),
                IsLeaf = !parents.Contains(entry.Code)
            };
            byCode[key] = entry;

            var parentKey = entry.ParentCode ?? string.Empty;
            if (!children.TryGetValue(parentKey, out var siblings))
            {
                siblings = new List<AgentClassificationEntry>();
                children[parentKey] = siblings;
            }

            siblings.Add(entry);
        }

        var ordered = new List<AgentClassificationEntry>(byCode.Count);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(string.Empty);

        // A parent cycle keeps its rows out of the walk; they stay addressable at the end.
        foreach (var entry in byCode.Values)
        {
            if (visited.Add(entry.Code))
                ordered.Add(entry);
        }

        return new AgentClassificationTable(
            string.IsNullOrWhiteSpace(source) ? null : source!.Trim(),
            ordered,
            byCode);

        void Walk(string parentKey)
        {
            if (!children.TryGetValue(parentKey, out var rowChildren))
                return;

            foreach (var child in rowChildren.OrderBy(entry => entry.Code, CodeComparer))
            {
                if (!visited.Add(child.Code))
                    continue;

                ordered.Add(child);
                Walk(child.Code);
            }
        }
    }

    /// <summary>Row of the exact code; null when the table does not contain it.</summary>
    public AgentClassificationEntry? Find(string? code)
    {
        var key = code?.Trim();
        return !string.IsNullOrEmpty(key) && _byCode.TryGetValue(key!, out var entry) ? entry : null;
    }

    /// <summary>
    /// Rows of the table in tree order, narrowed by a subtree, a level, the leaf
    /// flag and a case-insensitive substring of the code, description or breadcrumb.
    /// </summary>
    public IReadOnlyList<AgentClassificationEntry> Search(
        string? contains = null,
        string? parentCode = null,
        int? level = null,
        bool leafOnly = false,
        int limit = 50,
        int offset = 0)
    {
        var filter = string.IsNullOrWhiteSpace(contains) ? null : contains!.Trim();
        var root = string.IsNullOrWhiteSpace(parentCode) ? null : parentCode!.Trim();

        IEnumerable<AgentClassificationEntry> rows = Entries;
        if (root is not null)
            rows = rows.Where(entry => InSubtree(entry.Code, root));

        if (level.HasValue)
            rows = rows.Where(entry => entry.Level == level.Value);

        if (leafOnly)
            rows = rows.Where(entry => entry.IsLeaf);

        if (filter is not null)
        {
            rows = rows.Where(entry =>
                entry.Code.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.Description.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.Breadcrumb.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        return rows
            .Skip(System.Math.Max(0, offset))
            .Take(System.Math.Max(0, limit))
            .ToList();
    }

    /// <summary>
    /// Rows a missing code was most likely meant to be: the siblings of that code
    /// first, then the codes closest to it by edit distance.
    /// </summary>
    public IReadOnlyList<AgentClassificationEntry> Suggest(string? code, int max = 8)
    {
        if (max <= 0 || Entries.Count == 0)
            return Array.Empty<AgentClassificationEntry>();

        var requested = (code ?? string.Empty).Trim();
        var dot = requested.LastIndexOf('.');
        var parent = dot > 0 ? requested.Substring(0, dot) : null;

        var suggestions = new List<AgentClassificationEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries.Where(entry =>
                     string.Equals(entry.ParentCode, parent, StringComparison.OrdinalIgnoreCase)))
        {
            if (seen.Add(entry.Code))
                suggestions.Add(entry);
        }

        var nearest = Entries
            .Select(entry => (Entry: entry, Distance: Distance(requested, entry.Code)))
            .Where(pair => pair.Distance <= System.Math.Max(2, requested.Length / 3))
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Entry.Code, CodeComparer);

        foreach (var pair in nearest)
        {
            if (seen.Add(pair.Entry.Code))
                suggestions.Add(pair.Entry);
        }

        return suggestions.Take(max).ToList();
    }

    private bool InSubtree(string code, string root)
    {
        string? current = code;
        var guard = 0;
        while (current is not null && guard++ <= Entries.Count)
        {
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                return true;

            current = _byCode.TryGetValue(current, out var entry) ? entry.ParentCode : null;
        }

        return false;
    }

    private static string BreadcrumbOf(
        string code,
        Dictionary<string, AgentClassificationEntry> byCode,
        Dictionary<string, string> cache)
    {
        if (cache.TryGetValue(code, out var cached))
            return cached;

        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = code;
        while (current is not null
               && byCode.TryGetValue(current, out var entry)
               && seen.Add(current))
        {
            chain.Add(string.IsNullOrWhiteSpace(entry.Description) ? entry.Code : entry.Description);
            current = entry.ParentCode;
        }

        chain.Reverse();
        var breadcrumb = string.Join(BreadcrumbSeparator, chain);
        cache[code] = breadcrumb;
        return breadcrumb;
    }

    private static int Distance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
            previous[index] = index;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = System.Math.Min(
                    System.Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            var swap = previous;
            previous = current;
            current = swap;
        }

        return previous[right.Length];
    }

    private sealed class CodeComparison : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
                return 0;

            if (x is null)
                return -1;

            if (y is null)
                return 1;

            var left = x.Split('.');
            var right = y.Split('.');
            var shared = System.Math.Min(left.Length, right.Length);
            for (var index = 0; index < shared; index++)
            {
                var order = long.TryParse(left[index], out var leftNumber)
                            && long.TryParse(right[index], out var rightNumber)
                    ? leftNumber.CompareTo(rightNumber)
                    : string.Compare(left[index], right[index], StringComparison.OrdinalIgnoreCase);
                if (order != 0)
                    return order;
            }

            return left.Length.CompareTo(right.Length);
        }
    }
}

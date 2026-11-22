using System.Text.RegularExpressions;

namespace SmartCon.Agent.Services.Tools.Semantic;

/// <summary>A single classifier entry with its breadcrumb path.</summary>
internal sealed record ClassifierItem(
    string Code,
    string Name,
    string Breadcrumb,
    int Level,
    int Line,
    List<string> Children);

/// <summary>Outcome of parsing a hierarchical classifier text.</summary>
internal sealed class ClassifierParseResult
{
    public List<ClassifierItem> Items { get; } = new();

    public int TotalLines { get; init; }

    public bool Truncated { get; init; }

    public int SkippedLines { get; set; }

    public List<int> ParsedLevels =>
        Items.Select(item => item.Level).Distinct().OrderBy(level => level).ToList();
}

/// <summary>
/// Parser of hierarchical txt classifiers (real-cases benchmark v2, case 5).
/// A coded line starts with its code, e.g. "30.20.10 Краны шаровые" or
/// "A1010100 Wall Foundations". Three code forms are recognised: two-digit
/// dot separated segments (1 to 3), dot separated numeric segments of 1 to 4
/// digits (2 or more) and a prefix of 1 to 3 ASCII letters with digits and
/// optional dot separated groups. A dotted code takes its level from the
/// segment count (ОКС/UniFormat convention); a letter-prefixed code without
/// dots carries no level in the code itself, so its level comes from the
/// indentation - one step below the nearest coded line with a smaller indent.
/// Codes are never normalised: they are written back into the model. Lines
/// without a code that sit on a deeper indentation become children of the
/// previous coded line (keynote-style files); their text enriches the parent
/// entry but never becomes a selectable code. Breadcrumbs are stitched as
/// "раздел › подраздел › элемент" so a leaf carries its whole path - exactly
/// what a semantic matcher needs.
/// </summary>
internal static class ClassifierTextParser
{
    public const string BreadcrumbSeparator = " › ";

    /// <summary>Levels the parent stack holds before it has to grow.</summary>
    private const int InitialDepth = 16;

    /// <summary>
    /// Letter-prefixed code at line start: 1 to 3 ASCII letters, digits, then
    /// optional dot separated groups of letters/digits (A1010, A30.160).
    /// </summary>
    private static readonly Regex LetterCodeRegex =
        new(@"^[A-Za-z]{1,3}\d+(?:\.[A-Za-z\d]+)*(?![A-Za-z\d])", RegexOptions.Compiled);

    /// <summary>
    /// Numeric code at line start: dot separated segments of 1 to 4 digits,
    /// 2 or more of them (30.20.10.10, 23.10.05.10.11.11).
    /// </summary>
    private static readonly Regex NumericCodeRegex =
        new(@"^\d{1,4}(?:\.\d{1,4})+(?!\d)", RegexOptions.Compiled);

    /// <summary>Code at line start: 2-digit segments, 1 to 3 of them, dot separated.</summary>
    private static readonly Regex LegacyCodeRegex =
        new(@"^(\d{2}(?:\.\d{2}){0,2})(?!\d)", RegexOptions.Compiled);

    /// <summary>Characters allowed between the code and the name.</summary>
    private static readonly char[] Separators =
        { ' ', '\t', ')', '(', '.', ';', ':', '-', '–', '—', '=', '_' };

    public static ClassifierParseResult Parse(string content, int maxLines)
    {
        var allLines = SplitLines(content);
        var truncated = allLines.Count > maxLines;
        var lines = truncated ? allLines.GetRange(0, maxLines) : allLines;

        var result = new ClassifierParseResult
        {
            TotalLines = allLines.Count,
            Truncated = truncated
        };

        // parents[level] = name of the nearest previously seen coded line of
        // that level; deeper slots reset when a shallower line arrives.
        // indents[level] = indentation of that line.
        var parents = new string?[InitialDepth];
        var indents = new int[InitialDepth];
        ClassifierItem? lastItem = null;
        var lastItemIndent = -1;

        for (var index = 0; index < lines.Count; index++)
        {
            var raw = lines[index];
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
                continue;

            if (trimmed.StartsWith("#", StringComparison.Ordinal)
                || trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;

            var indent = IndentWidth(raw);
            var letterMatch = LetterCodeRegex.Match(trimmed);
            var match = letterMatch.Success ? letterMatch : MatchNumericCode(trimmed);
            if (match.Success)
            {
                var code = match.Value;
                var name = trimmed.Substring(code.Length).TrimStart(Separators).Trim();
                if (name.Length == 0)
                {
                    result.SkippedLines++;
                    continue;
                }

                var level = letterMatch.Success && code.IndexOf('.') < 0
                    ? LevelByIndent(parents, indents, lastItem?.Level ?? 0, indent)
                    : code.Split('.').Length;
                if (level >= parents.Length)
                    GrowParents(ref parents, ref indents, level);

                var item = new ClassifierItem(
                    code, name, BuildBreadcrumb(parents, level, name), level,
                    index + 1, new List<string>());
                result.Items.Add(item);

                lastItem = item;
                lastItemIndent = indent;
                parents[level] = name;
                indents[level] = indent;
                for (var deeper = level + 1; deeper < parents.Length; deeper++)
                    parents[deeper] = null;
            }
            else if (lastItem is not null && indent > lastItemIndent)
            {
                lastItem.Children.Add(trimmed);
            }
            else
            {
                result.SkippedLines++;
            }
        }

        return result;
    }

    /// <summary>
    /// Numeric code at line start. The multi-segment form is tried before the
    /// legacy one: alone, the legacy regex cuts a longer code (30.20.10.10,
    /// 10.10.100) short and the cut-off tail leaks into the name.
    /// </summary>
    private static Match MatchNumericCode(string trimmed)
    {
        var match = NumericCodeRegex.Match(trimmed);
        return match.Success ? match : LegacyCodeRegex.Match(trimmed);
    }

    /// <summary>
    /// Level of a letter-prefixed code without dots: one below the nearest
    /// coded line with a smaller indent, or 1 when there is none. The step
    /// width is not guessed - any deeper indent is one step.
    /// </summary>
    private static int LevelByIndent(string?[] parents, int[] indents, int lastLevel, int indent)
    {
        for (var depth = lastLevel; depth >= 1; depth--)
        {
            if (parents[depth] is not null && indents[depth] < indent)
                return depth + 1;
        }

        return 1;
    }

    private static void GrowParents(ref string?[] parents, ref int[] indents, int level)
    {
        var capacity = parents.Length;
        while (capacity <= level)
            capacity *= 2;

        Array.Resize(ref parents, capacity);
        Array.Resize(ref indents, capacity);
    }

    private static string BuildBreadcrumb(string?[] parents, int level, string name)
    {
        var parts = new List<string>(level);
        for (var depth = 1; depth < level; depth++)
        {
            if (parents[depth] is not null)
                parts.Add(parents[depth]!);
        }

        parts.Add(name);
        return string.Join(BreadcrumbSeparator, parts);
    }

    private static int IndentWidth(string line)
    {
        var width = 0;
        foreach (var character in line)
        {
            if (character == ' ')
                width++;
            else if (character == '\t')
                width += 4;
            else
                break;
        }

        return width;
    }

    /// <summary>
    /// Physical split that keeps empty lines so item line numbers match the
    /// file, and treats CRLF, LF and lone CR as one break.
    /// </summary>
    private static List<string> SplitLines(string content)
    {
        var lines = new List<string>();
        var current = new System.Text.StringBuilder();
        char previous = '\0';

        foreach (var character in content)
        {
            if (character == '\r' || character == '\n')
            {
                if (character == '\n' && previous == '\r')
                {
                    previous = character;
                    continue;
                }

                lines.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }

            previous = character;
        }

        lines.Add(current.ToString());

        // A trailing line break does not start a new line: "a\nb\n" is two
        // lines, not three, and TotalLines must not count the phantom.
        if (lines.Count > 0 && lines[lines.Count - 1].Length == 0
            && (previous == '\r' || previous == '\n'))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}

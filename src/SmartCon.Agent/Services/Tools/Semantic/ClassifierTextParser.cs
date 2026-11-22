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
/// A coded line looks like "30.20.10 Краны шаровые": the code is two-digit dot
/// separated segments and the level equals the segment count (ОКС/UniFormat
/// convention). Lines without a code that sit on a deeper indentation become
/// children of the previous coded line (keynote-style files); their text
/// enriches the parent entry but never becomes a selectable code. Breadcrumbs
/// are stitched as "раздел › подраздел › элемент" so a leaf carries its whole
/// path - exactly what a semantic matcher needs.
/// </summary>
internal static class ClassifierTextParser
{
    public const string BreadcrumbSeparator = " › ";

    /// <summary>Code at line start: 2-digit segments, 1 to 3 of them, dot separated.</summary>
    private static readonly Regex CodeRegex =
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
        var parents = new string?[4];
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
            var match = CodeRegex.Match(trimmed);
            if (match.Success)
            {
                var code = match.Groups[1].Value;
                var name = trimmed.Substring(match.Length).TrimStart(Separators).Trim();
                if (name.Length == 0)
                {
                    result.SkippedLines++;
                    continue;
                }

                var level = code.Split('.').Length;
                var item = new ClassifierItem(
                    code, name, BuildBreadcrumb(parents, level, name), level,
                    index + 1, new List<string>());
                result.Items.Add(item);

                lastItem = item;
                lastItemIndent = indent;
                parents[level] = name;
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

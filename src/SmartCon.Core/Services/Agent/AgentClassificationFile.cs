using System.Globalization;
using System.Text;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// File format of a classification table as Revit loads it: one row per line,
/// four tab-separated columns «code, description, level, category id» — the
/// last one may stay empty, the tab may not be missing — and line breaks in
/// either style. Pure logic; the Revit side reads the file through it before it
/// asks Revit to load it, because Revit only reports a line of another shape as
/// «cannot understand».
/// </summary>
public static class AgentClassificationFile
{
    /// <summary>The Assembly Code tree has five levels at most.</summary>
    public const int MaxLevel = 5;

    /// <summary>Malformed lines a parse reports by number; the rest is only counted.</summary>
    public const int MaxReportedErrors = 10;

    private static readonly UTF8Encoding Utf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Encoding Utf16 = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);

    public static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    public static bool HasUtf16Bom(byte[] bytes) =>
        bytes.Length >= 2
        && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));

    /// <summary>
    /// Reads the text of a table file: UTF-16 by its byte order mark, otherwise
    /// strict UTF-8. A file in a legacy single-byte encoding is refused with an
    /// error instead of being guessed: its non-ASCII descriptions would be
    /// unreadable in the document.
    /// </summary>
    public static string? Decode(byte[] bytes, out string? error)
    {
        error = null;
        if (HasUtf16Bom(bytes))
        {
            var bigEndian = bytes[0] == 0xFE;
            var body = new byte[bytes.Length - 2];
            Array.Copy(bytes, 2, body, 0, body.Length);
            return new UnicodeEncoding(bigEndian, byteOrderMark: false).GetString(body);
        }

        try
        {
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            error = "the file is neither UTF-16 nor UTF-8 (a legacy single-byte encoding): " +
                    "save it as UTF-16 (\"Unicode\" in Notepad) and load it again";
            return null;
        }
    }

    /// <summary>UTF-16 with a byte order mark: the encoding Revit reads correctly, Cyrillic included.</summary>
    public static byte[] EncodeUtf16(string text) => Utf16.GetPreamble().Concat(Utf16.GetBytes(text)).ToArray();

    /// <summary>UTF-8 without a byte order mark: enough for a table of plain ASCII text.</summary>
    public static byte[] EncodeUtf8(string text) => Utf8.GetBytes(text);

    /// <summary>
    /// Reads the rows of a table file. A line that is not «code, description,
    /// level, category id» is counted as malformed and reported; empty lines
    /// are skipped, columns after the fourth are ignored.
    /// </summary>
    public static ParseResult Parse(string? text)
    {
        var rows = new List<AgentClassificationEntry>();
        var errors = new List<string>();
        var malformed = 0;
        if (string.IsNullOrEmpty(text))
            return new ParseResult(rows, errors, 0);

        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length > 0 && line[line.Length - 1] == '\r')
                line = line.Substring(0, line.Length - 1);

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var number = (index + 1).ToString(CultureInfo.InvariantCulture);
            var columns = line.Split('\t');
            if (columns.Length < 4)
            {
                Report(errors, malformed++, "line " + number + ": expected four tab-separated columns " +
                                            "(code, description, level, category id — the last one may be " +
                                            "empty), found " + columns.Length +
                                            (line.IndexOf(' ') >= 0
                                                ? " — the columns are separated by spaces, not tabs"
                                                : string.Empty));
                continue;
            }

            var code = columns[0].Trim();
            if (code.Length == 0)
            {
                Report(errors, malformed++, "line " + number + ": the code is empty");
                continue;
            }

            var levelText = columns[2].Trim();
            if (!int.TryParse(levelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
                || level < 1
                || level > MaxLevel)
            {
                Report(errors, malformed++, "line " + number + ": level '" + levelText +
                                            "' is not a number from 1 to " + MaxLevel);
                continue;
            }

            var categoryText = columns[3].Trim();
            long? categoryId = long.TryParse(categoryText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var category)
                ? category
                : null;
            rows.Add(new AgentClassificationEntry
            {
                Code = code,
                Description = columns[1].Trim(),
                Level = level,
                CategoryId = categoryId
            });
        }

        return new ParseResult(rows, errors, malformed);
    }

    /// <summary>
    /// Whether the table Revit loaded carries the descriptions of the file. A
    /// description that contains only ASCII always counts as read: Revit may
    /// normalize a character there. A non-ASCII description that came out
    /// different means the file's encoding was not understood — the text is
    /// unreadable in the document, whatever the codes look like.
    /// </summary>
    public static bool DescriptionsMatch(
        IReadOnlyList<AgentClassificationEntry> rows,
        AgentClassificationTable table,
        out string? example)
    {
        example = null;
        foreach (var row in rows)
        {
            if (row.Description.All(character => character < 0x80))
                continue;

            var loaded = table.Find(row.Code);
            if (loaded is null || string.Equals(loaded.Description.Trim(), row.Description, StringComparison.Ordinal))
                continue;

            example = "code '" + row.Code + "': the file says '" + row.Description +
                      "', the document reads '" + loaded.Description + "'";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Writes rows in the table file format, one line each, in the order given
    /// (a parent has to precede its children). Tabs and line breaks inside a
    /// code or description would split the row, so they become spaces.
    /// </summary>
    public static string Format(IEnumerable<AgentClassificationEntry> rows)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            AppendCell(builder, row.Code);
            builder.Append('\t');
            AppendCell(builder, row.Description);
            builder.Append('\t');
            builder.Append(row.Level.ToString(CultureInfo.InvariantCulture));
            builder.Append('\t');
            if (row.CategoryId.HasValue)
                builder.Append(row.CategoryId.Value.ToString(CultureInfo.InvariantCulture));

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static void AppendCell(StringBuilder builder, string text)
    {
        foreach (var character in text)
            builder.Append(character == '\t' || character == '\n' || character == '\r' ? ' ' : character);
    }

    private static void Report(List<string> errors, int malformed, string error)
    {
        if (malformed < MaxReportedErrors)
            errors.Add(error);
    }

    /// <summary>Rows of a parsed table file and the lines that are not rows.</summary>
    public sealed class ParseResult
    {
        internal ParseResult(
            List<AgentClassificationEntry> rows,
            List<string> errors,
            int malformedLines)
        {
            Rows = rows;
            Errors = errors;
            MalformedLines = malformedLines;
        }

        /// <summary>Lines shaped like a table row, in file order.</summary>
        public IReadOnlyList<AgentClassificationEntry> Rows { get; }

        /// <summary>The first malformed lines, each naming its line number.</summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>How many non-empty lines are not rows.</summary>
        public int MalformedLines { get; }

        /// <summary>
        /// True when the file is not a classification table at all: no row, or
        /// more malformed lines than rows. A few bad lines among many rows are
        /// left to Revit, which skips them and reports them itself.
        /// </summary>
        public bool IsWrongFormat => Rows.Count == 0 || MalformedLines > Rows.Count;
    }
}

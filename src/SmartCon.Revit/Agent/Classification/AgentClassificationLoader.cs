using System.Text;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Revit.Agent.Classification;

/// <summary>
/// Connects a classification file to the document — what Manage &gt;
/// Classification Settings does — by loading it into the AssemblyCodeTable.
/// Runs in the caller's transaction (a dry run is rolled back with it). The
/// file is checked against the table format before Revit sees it, and the
/// table Revit loaded is read back and compared with the file: Revit reports
/// success for a file it understood no line of, and reads a non-ASCII file of
/// the wrong encoding as unreadable text without a word.
/// </summary>
internal static class AgentClassificationLoader
{
    internal const string ExpectedFormat =
        "Expected format: one row per line, four tab-separated columns — code, description, " +
        "level (1-5), category id (may be empty) — saved as UTF-16 (\"Unicode\" in Notepad) " +
        "when the descriptions are not plain ASCII, or as ASCII / UTF-8 without BOM otherwise.";

    /// <summary>Problems reported one by one; the rest is summed up.</summary>
    internal const int MaxReportedProblems = 10;

    internal enum Status
    {
        /// <summary>The document now loads its table from the file.</summary>
        Loaded,

        /// <summary>The document already carried exactly this table from this file.</summary>
        AlreadyCurrent,

        FileNotFound,

        /// <summary>The file is not a classification table Revit can read (encoding, separators, no rows).</summary>
        InvalidFormat,

        /// <summary>
        /// Revit loaded the rows, but their non-ASCII descriptions came out as
        /// other text: the encoding of the file is not understood here.
        /// </summary>
        UnreadableText,

        /// <summary>Revit refused the file or loaded no entry from it.</summary>
        LoadFailed
    }

    internal sealed class Result
    {
        public Status Status { get; init; }

        public string Path { get; init; } = string.Empty;

        /// <summary>Display path of the table the document loaded before the call.</summary>
        public string? PreviousSource { get; init; }

        /// <summary>Display path of the table the document carries after the call.</summary>
        public string? Source { get; init; }

        public int EntryCount { get; init; }

        public int LeafCount { get; init; }

        /// <summary>Why the status is what it is: lines Revit skipped, rows it could not place, an unreadable description.</summary>
        public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    }

    internal static Result Load(Document doc, string absolutePath)
    {
        var path = absolutePath.Trim();
        if (!File.Exists(path))
            return new Result { Status = Status.FileNotFound, Path = path };

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Invalid(path, "The file could not be read: " + ex.Message);
        }

        if (AgentClassificationFile.HasUtf8Bom(bytes))
        {
            return Invalid(path, "line 1: the file starts with a UTF-8 byte order mark (BOM), which " +
                                 "corrupts the first code; save it as UTF-16 (\"Unicode\" in Notepad) " +
                                 "when the descriptions are not plain ASCII, otherwise as UTF-8 without BOM");
        }

        var text = AgentClassificationFile.Decode(bytes, out var decodeError);
        if (text is null)
            return Invalid(path, decodeError!);

        var parsed = AgentClassificationFile.Parse(text);
        if (parsed.IsWrongFormat)
        {
            return Invalid(path, parsed.MalformedLines == 0
                ? "the file has no classification rows"
                : string.Join("; ", parsed.Errors));
        }

        var problems = new List<string>(parsed.Errors);
        AgentClassificationTable previous;
        AgentClassificationTable current;
        ExternalResourceLoadStatus status;
        try
        {
            previous = AgentClassificationReader.Read(doc);
            var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
            using var reference = ExternalResourceReference.CreateLocalResource(
                doc,
                ExternalResourceTypes.BuiltInExternalResourceTypes.AssemblyCodeTable,
                modelPath,
                PathType.Absolute);
            using var results = new KeyBasedTreeEntriesLoadResults();
            status = AssemblyCodeTable.GetAssemblyCodeTable(doc).LoadFrom(reference, results);
            Collect(results, problems);
            current = AgentClassificationReader.Read(doc);
        }
        catch (Exception ex)
        {
            return new Result
            {
                Status = Status.LoadFailed,
                Path = path,
                Problems = Add(problems, ex.GetType().Name + ": " + ex.Message)
            };
        }

        if (status is not (ExternalResourceLoadStatus.Success or ExternalResourceLoadStatus.ResourceAlreadyCurrent))
        {
            return new Result
            {
                Status = Status.LoadFailed,
                Path = path,
                PreviousSource = previous.Source,
                Problems = Add(problems, "Revit reported " + status + ".")
            };
        }

        if (!current.Loaded)
        {
            return new Result
            {
                Status = Status.LoadFailed,
                Path = path,
                PreviousSource = previous.Source,
                Problems = Add(problems, "Revit loaded no classification entry from the file.")
            };
        }

        // Rows the file has that the table lacks were reported by Revit above.
        if (!AgentClassificationFile.DescriptionsMatch(parsed.Rows, current, out var example))
        {
            return new Result
            {
                Status = Status.UnreadableText,
                Path = path,
                PreviousSource = previous.Source,
                Problems = Add(problems, example!)
            };
        }

        return new Result
        {
            Status = IsSameTable(previous, current, path) ? Status.AlreadyCurrent : Status.Loaded,
            Path = path,
            PreviousSource = previous.Source,
            Source = current.Source,
            EntryCount = current.Entries.Count,
            LeafCount = current.Entries.Count(entry => entry.IsLeaf),
            Problems = problems
        };
    }

    private static bool IsSameTable(AgentClassificationTable previous, AgentClassificationTable current, string path)
    {
        if (!previous.Loaded
            || !string.Equals(previous.Source, path, StringComparison.OrdinalIgnoreCase)
            || previous.Entries.Count != current.Entries.Count)
        {
            return false;
        }

        for (var index = 0; index < previous.Entries.Count; index++)
        {
            var before = previous.Entries[index];
            var after = current.Entries[index];
            if (!string.Equals(before.Code, after.Code, StringComparison.Ordinal)
                || !string.Equals(before.Description, after.Description, StringComparison.Ordinal)
                || before.Level != after.Level)
            {
                return false;
            }
        }

        return true;
    }

    private static string[] Collect(KeyBasedTreeEntriesLoadResults results, List<string> problems)
    {
        foreach (var record in results.GetFileSyntaxErrors())
            Add(problems, "Revit could not parse the line: " + record);

        foreach (var file in results.GetFileReadErrors())
            Add(problems, "Revit could not read the file: " + file);

        foreach (var error in results.GetKeyBasedTreeEntryErrors())
        {
            string key;
            try
            {
                key = error.GetEntry()?.Key ?? "?";
            }
            catch (Exception)
            {
                key = "?";
            }

            Add(problems, "Revit skipped the code '" + key + "' (" + error.ErrorType + ")");
        }

        return problems.ToArray();
    }

    /// <summary>Adds one problem while there is room; the last line sums up the rest.</summary>
    private static string[] Add(List<string> problems, string problem)
    {
        if (problems.Count < MaxReportedProblems)
        {
            problems.Add(problem);
            return problems.ToArray();
        }

        var overflow = problems.Count - MaxReportedProblems + 1;
        problems[MaxReportedProblems - 1] = "... and " + (overflow + 1) + " more problem(s)";
        return problems.ToArray();
    }

    private static Result Invalid(string path, params string[] problems) => new()
    {
        Status = Status.InvalidFormat,
        Path = path,
        Problems = problems
    };
}

using System.Text;
using Autodesk.Revit.DB;
using SmartCon.Core.Services.Agent;
using SmartCon.Revit.Agent.Classification;

namespace SmartCon.IntegrationTests.Agent.Support;

/// <summary>
/// Classification table files of the agent integration tests: the tab-separated
/// format of Revit's own classification export — four columns (code,
/// description, level, category id), the fourth often empty. The fixture
/// directory comes from SMARTCON_IT_FIXTURE_DIR; without it the files go next
/// to the test assembly. Never <see cref="Path.GetTempPath()"/>: inside the
/// Revit host it is not the user's temp.
/// </summary>
internal static class ClassificationFixtures
{
    public const string DirectoryVariable = "SMARTCON_IT_FIXTURE_DIR";

    /// <summary>A small three-level table without category ids.</summary>
    public const string SampleTable =
        "10\tShell\t1\t\n" +
        "10.10\tWalls\t2\t\n" +
        "10.10.10\tLoad-bearing walls\t3\t\n" +
        "10.10.20\tPartitions\t3\t\n" +
        "20\tServices\t1\t\n" +
        "20.10\tPipelines\t2\t\n" +
        "20.10.10\tPipes\t3\t\n" +
        "20.20\tValves\t2\t\n" +
        "20.20.10\tBall valves\t3\t\n" +
        "20.20.20\tGate valves\t3\t\n" +
        "30\tOther\t1\t\n";

    /// <summary>Codes of <see cref="SampleTable"/> that have no children.</summary>
    public static readonly string[] LeafCodes =
        { "10.10.10", "10.10.20", "20.10.10", "20.20.10", "20.20.20", "30" };

    /// <summary>Codes of <see cref="SampleTable"/> that have children.</summary>
    public static readonly string[] BranchCodes = { "10", "10.10", "20", "20.10", "20.20" };

    /// <summary>The sample codes with other names: a second table for the same document.</summary>
    public const string RenamedTable =
        "10\tEnvelope\t1\t\n" +
        "10.10\tWall assemblies\t2\t\n" +
        "10.10.10\tExterior walls\t3\t\n" +
        "10.10.20\tInterior partitions\t3\t\n" +
        "20\tBuilding services\t1\t\n" +
        "20.10\tPipework\t2\t\n" +
        "20.10.10\tPipe runs\t3\t\n" +
        "20.20\tPipeline valves\t2\t\n" +
        "20.20.10\tShut-off ball valves\t3\t\n" +
        "20.20.20\tGate valves\t3\t\n" +
        "30\tMiscellaneous\t1\t\n";

    /// <summary>Cyrillic descriptions: the file has to be UTF-8 for them to survive.</summary>
    public const string CyrillicTable =
        "10\tОболочка\t1\t\n" +
        "10.10\tСтены\t2\t\n" +
        "10.10.10\tНесущие стены\t3\t\n" +
        "20\tИнженерные системы\t1\t\n" +
        "20.10\tТрубопроводы\t2\t\n" +
        "20.10.10\tТрубы\t3\t\n";

    /// <summary>
    /// Only three columns (no category column): the layout of a txt classifier
    /// or a file trimmed by a spreadsheet. Revit does not understand a line of
    /// this shape.
    /// </summary>
    public const string ThreeColumnTable =
        "10\tShell\t1\n" +
        "10.10\tWalls\t2\n" +
        "10.10.10\tLoad-bearing walls\t3\n";

    /// <summary>The «code name level» layout of an indented txt classifier.</summary>
    public const string SpaceSeparatedTable =
        "10 Shell 1\n" +
        "\t10.10 Walls 2\n" +
        "\t\t10.10.10 Load-bearing walls 3\n";

    /// <summary>
    /// Two rows Revit has to skip — a level-3 row right after a level-1 row
    /// (no parent) and a repeated code — among valid ones.
    /// </summary>
    public const string BrokenRowsTable =
        "10\tShell\t1\t\n" +
        "10.10\tWalls\t2\t\n" +
        "10.10.10\tLoad-bearing walls\t3\t\n" +
        "20\tServices\t1\t\n" +
        "20.20.10\tOrphan\t3\t\n" +
        "10.10\tDuplicate\t2\t\n";

    /// <summary>Category ids: the id of OST_Walls and an id that is no category.</summary>
    public const string CategoryTable =
        "10\tShell\t1\t\n" +
        "10.10\tWalls\t2\t-2000011\n" +
        "10.10.10\tLoad-bearing walls\t3\t-2000011\n" +
        "10.10.20\tPartitions\t3\t-2000011\n" +
        "20\tOdd category\t1\t123456789\n" +
        "20.10\tText category\t2\tnot-a-number\n";

    public static string Directory()
    {
        var configured = Environment.GetEnvironmentVariable(DirectoryVariable);
        var directory = !string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured!)
            ? configured!
            : Path.Combine(AppContext.BaseDirectory, "Agent", "ClassificationFixtures");
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    public static readonly Encoding Utf8 = new UTF8Encoding(false);
    public static readonly Encoding Utf8Bom = new UTF8Encoding(true);

    /// <summary>The encoding Revit reads correctly, Cyrillic included.</summary>
    public static readonly Encoding Utf16 = new UnicodeEncoding(false, true);

    /// <summary>A legacy single-byte encoding: what an old ANSI file looks like.</summary>
    public static Encoding Windows1251()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("windows-1251");
    }

    /// <summary>Writes a table file in the given encoding, byte order mark included (UTF-16 by default).</summary>
    public static string WriteTable(string fileName, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(Directory(), fileName);
        var encoder = encoding ?? Utf16;
        var preamble = encoder.GetPreamble();
        var body = encoder.GetBytes(text);
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static string ToCrLf(string text) => text.Replace("\n", "\r\n");

    public static string Table(AgentClassificationTable table) =>
        table.Entries.Count + " entries, source '" + table.Source + "': " +
        string.Join(" | ", table.Entries.Take(12).Select(entry =>
            entry.Code + "=" + entry.Description + " L" + entry.Level +
            (string.IsNullOrEmpty(entry.ParentCode) ? string.Empty : " ^" + entry.ParentCode) +
            (entry.CategoryId.HasValue ? " cat" + entry.CategoryId.Value : string.Empty)));
}

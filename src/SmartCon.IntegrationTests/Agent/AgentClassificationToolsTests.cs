using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.IntegrationTests.Agent.Support;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Classification;
using SmartCon.Revit.Agent.Query;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// The classification capabilities of the agent against the table the document
/// really carries: the table loader tool, the entries reader, the overview
/// block, and — in the other partials — the classification check of
/// set_parameters (tool and job action). Every test starts from a fresh
/// document without a classifier.
/// </summary>
public sealed partial class AgentClassificationToolsTests : RevitApiTest
{
    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private List<long> _wallTypeIds = null!;

    private Document Doc => _document!;

    private long WallTypeId => _wallTypeIds[0];

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _wallTypeIds = new FilteredElementCollector(Doc)
            .OfClass(typeof(WallType))
            .WhereElementIsElementType()
            .Select(type => type.Id.GetValue())
            .ToList();
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
        CleanupJobDatabase();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static JsonElement Args(object value)
    {
        using var document = JsonSerializer.SerializeToDocument(value);
        return document.RootElement.Clone();
    }

    private void RequireWallType()
    {
        if (_wallTypeIds.Count == 0)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");
    }

    private ToolResult List(object args) =>
        new ListClassificationEntriesTool(new StubRevitContext(Doc))
            .ExecuteForTests(Doc, Args(args), CancellationToken.None);

    private ToolResult Load(string path, bool dryRun) =>
        new LoadClassificationTableTool(new StubRevitContext(Doc), _transactions!, _changeSets!)
            .ExecuteForTests(Args(new { path, dryRun }), approved: true, CancellationToken.None);

    private ToolResult Set(object items, bool dryRun = false, bool allowUnverified = false, bool atomic = false) =>
        new SetParametersTool(new StubRevitContext(Doc), _transactions!, _changeSets!)
            .ExecuteForTests(
                Args(new
                {
                    items = JsonSerializer.Deserialize<JsonElement>((string)items),
                    dryRun,
                    allowUnverifiedClassification = allowUnverified,
                    atomic
                }),
                approved: true,
                CancellationToken.None);

    private static string SampleFile(string name, string text = ClassificationFixtures.SampleTable) =>
        ClassificationFixtures.WriteTable(name, text, ClassificationFixtures.Utf8);

    private static string CodeItem(long typeId, string value, string parameter = "ASSEMBLY_CODE") =>
        "{\"elementId\":" + typeId + ",\"parameter\":\"" + parameter + "\",\"value\":\"" + value +
        "\",\"scope\":\"type\"}";

    private string? CodeOf(long typeId) =>
        Doc.GetElement(ElementIdCompat.Create(typeId))?.get_Parameter(AgentParameterResolver.AssemblyCodeBuiltIn)?.AsString();

    private string? DescriptionOf(long typeId) =>
        Doc.GetElement(ElementIdCompat.Create(typeId))?.get_Parameter(AgentParameterResolver.AssemblyDescriptionBuiltIn)?.AsString();

    /// <summary>Codes of a page as one string: «10.10.10,10.10.20».</summary>
    private static string EntryCodes(ToolResult result) =>
        string.Join(",", result.Data!.Value.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("code").GetString() ?? string.Empty));

    // ── A document without a classifier ─────────────────────────────────

    [Test]
    public async Task List_WithoutTable_ReportsTableNotLoaded()
    {
        var result = List(new { });

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("classification_table_not_loaded");
        await Assert.That(result.Data!.Value.GetProperty("loaded").GetBoolean()).IsFalse();
        await Assert.That(result.Message).Contains("not loaded");
        await Assert.That(result.Message).Contains("load_classification_table");
        await Assert.That(result.Message).Contains("never pick codes from memory or by analogy");
    }

    [Test]
    public async Task Overview_WithoutTable_ReportsNotLoadedAndKeepsItsShape()
    {
        var result = new GetProjectOverviewTool(new StubRevitContext(Doc))
            .ExecuteForTests(Doc, Args(new { }), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var overview = result.Data!.Value;
        foreach (var key in new[] { "documentId", "title", "revitVersion", "units", "levelCount", "viewCount", "levels", "categories" })
            await Assert.That(overview.TryGetProperty(key, out _)).IsTrue();

        var classification = overview.GetProperty("classification");
        await Assert.That(classification.GetProperty("loaded").GetBoolean()).IsFalse();
        await Assert.That(classification.GetProperty("entries").GetInt32()).IsEqualTo(0);
        await Assert.That(classification.TryGetProperty("source", out _)).IsFalse();
    }

    // ── load_classification_table ────────────────────────────────────────

    [Test]
    public async Task Load_DryRun_ReportsTheResultAndLeavesTheTableEmpty()
    {
        var path = SampleFile("dry-run.txt");

        var result = Load(path, dryRun: true);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.ErrorCode).IsNull();
        await Assert.That(result.Data!.Value.GetProperty("dryRun").GetBoolean()).IsTrue();
        await Assert.That(result.Data.Value.GetProperty("entriesTotal").GetInt32()).IsEqualTo(11);
        await Assert.That(result.Data.Value.GetProperty("leafTotal").GetInt32()).IsEqualTo(6);
        await Assert.That(result.Data.Value.GetProperty("source").GetString()).IsEqualTo(path);
        await Assert.That(result.Data.Value.GetProperty("changed")[0].GetProperty("reason").GetString())
            .Contains("Dry run");

        var table = AgentClassificationReader.Read(Doc);
        await Assert.That(table.Loaded).IsFalse();
        await Assert.That(table.Source).IsNull();
        await Assert.That(Doc.IsModified).IsFalse();
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Load_Apply_ConnectsTheTableAndReadsItBack()
    {
        var path = SampleFile("apply.txt");

        var result = Load(path, dryRun: false);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("source").GetString()).IsEqualTo(path);
        await Assert.That(data.GetProperty("entriesTotal").GetInt32()).IsEqualTo(11);
        await Assert.That(data.GetProperty("leafTotal").GetInt32()).IsEqualTo(6);
        await Assert.That(data.GetProperty("undoSupported").GetBoolean()).IsFalse();
        await Assert.That(data.TryGetProperty("changeSetId", out _)).IsFalse();
        await Assert.That(result.Warnings.Any(warning => warning.Contains("undo_last cannot revert it"))).IsTrue();

        var table = AgentClassificationReader.Read(Doc);
        await Assert.That(table.Entries.Count).IsEqualTo(11);
        await Assert.That(table.Source).IsEqualTo(path);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Load_TheSameTableAgain_ChangesNothing()
    {
        var path = SampleFile("again.txt");
        var first = Load(path, dryRun: false);
        await Assert.That(first.Success).IsTrue();

        var second = Load(path, dryRun: false);

        await Assert.That(second.Success).IsTrue();
        await Assert.That(second.Data!.Value.GetProperty("noChanges").GetBoolean()).IsTrue();
        await Assert.That(second.Data.Value.GetProperty("errorCode").GetString()).IsEqualTo("no_changes");
        await Assert.That(AgentClassificationReader.Read(Doc).Entries.Count).IsEqualTo(11);
    }

    [Test]
    public async Task Load_AnotherTable_ReplacesThePreviousOne()
    {
        var first = Load(SampleFile("replace-a.txt"), dryRun: false);
        await Assert.That(first.Success).IsTrue();
        var secondPath = SampleFile("replace-b.txt", ClassificationFixtures.RenamedTable);

        var second = Load(secondPath, dryRun: false);

        await Assert.That(second.Success).IsTrue();
        await Assert.That(second.Data!.Value.GetProperty("changed")[0].GetProperty("oldValue").GetString())
            .IsEqualTo(SampleFile("replace-a.txt"));
        var table = AgentClassificationReader.Read(Doc);
        await Assert.That(table.Source).IsEqualTo(secondPath);
        await Assert.That(table.Find("10.10")!.Description).IsEqualTo("Wall assemblies");
    }

    [Test]
    public async Task Load_MissingFile_ReturnsFileNotFound()
    {
        var result = Load(Path.Combine(ClassificationFixtures.Directory(), "no-such-table.txt"), dryRun: false);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("file_not_found");
        await Assert.That(AgentClassificationReader.Read(Doc).Loaded).IsFalse();
    }

    [Test]
    public async Task Load_RelativePath_IsInvalidArguments()
    {
        var result = Load("classifier-table.txt", dryRun: true);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_arguments");
    }

    [Test]
    public async Task Load_ThreeColumnFile_IsInvalidFormat()
    {
        // Three columns is the layout of a txt classifier: Revit understands
        // none of its lines and still reports a successful load.
        var path = SampleFile("three-columns.txt", ClassificationFixtures.ThreeColumnTable);

        var result = Load(path, dryRun: false);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("classification_file_invalid");
        await Assert.That(result.Message).Contains("four tab-separated columns");
        await Assert.That(result.Message).Contains("line 1");
        await Assert.That(AgentClassificationReader.Read(Doc).Loaded).IsFalse();
    }

    [Test]
    public async Task Load_SpaceSeparatedFile_IsInvalidFormat()
    {
        var path = SampleFile("spaces.txt", ClassificationFixtures.SpaceSeparatedTable);

        var result = Load(path, dryRun: true);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("classification_file_invalid");
        await Assert.That(result.Message).Contains("spaces, not tabs");
    }

    [Test]
    public async Task Load_Utf8ByteOrderMark_IsInvalidFormat()
    {
        var path = ClassificationFixtures.WriteTable("bom.txt", ClassificationFixtures.SampleTable, ClassificationFixtures.Utf8Bom);

        var result = Load(path, dryRun: false);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("classification_file_invalid");
        await Assert.That(result.Message).Contains("byte order mark");
        await Assert.That(AgentClassificationReader.Read(Doc).Loaded).IsFalse();
    }

    [Test]
    public async Task Load_LegacySingleByteFile_IsInvalidFormat()
    {
        var path = ClassificationFixtures.WriteTable(
            "ansi.txt", ClassificationFixtures.CyrillicTable, ClassificationFixtures.Windows1251());

        var result = Load(path, dryRun: false);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("classification_file_invalid");
        await Assert.That(result.Message).Contains("UTF-16");
        await Assert.That(AgentClassificationReader.Read(Doc).Loaded).IsFalse();
    }

    [Test]
    public async Task Load_CyrillicDescriptions_AreReadableInTheDocument()
    {
        var path = ClassificationFixtures.WriteTable(
            "cyrillic.txt", ClassificationFixtures.CyrillicTable, ClassificationFixtures.Utf16);

        var result = Load(path, dryRun: false);
        await Assert.That(result.Success).IsTrue();

        var table = AgentClassificationReader.Read(Doc);
        await Assert.That(table.Find("10.10.10")!.Description).IsEqualTo("Несущие стены");
        await Assert.That(table.Find("20.10")!.Breadcrumb).IsEqualTo("Инженерные системы › Трубопроводы");

        var listed = List(new { contains = "трубы" });
        await Assert.That(listed.Success).IsTrue();
        await Assert.That(EntryCodes(listed)).IsEqualTo("20.10.10");
    }

    [Test]
    public async Task Load_CyrillicInUtf8_NeverLeavesUnreadableTextInTheDocument()
    {
        // Whether this Revit reads UTF-8 descriptions or not is not ours to
        // decide; what must hold either way is that the document never ends up
        // with a table whose text nobody can read.
        var path = ClassificationFixtures.WriteTable(
            "cyrillic-utf8.txt", ClassificationFixtures.CyrillicTable, ClassificationFixtures.Utf8);

        var result = Load(path, dryRun: false);

        var table = AgentClassificationReader.Read(Doc);
        if (result.Success)
        {
            await Assert.That(table.Find("10.10.10")!.Description).IsEqualTo("Несущие стены");
            return;
        }

        await Assert.That(result.ErrorCode).IsEqualTo("classification_file_invalid");
        await Assert.That(result.Message).Contains("UTF-16");
        await Assert.That(table.Loaded).IsFalse();
        await Assert.That(Doc.IsModified).IsFalse();
    }

    [Test]
    public async Task Load_FileWithRowsRevitSkips_LoadsTheRestAndReportsThem()
    {
        var path = SampleFile("broken-rows.txt", ClassificationFixtures.BrokenRowsTable);

        var result = Load(path, dryRun: false);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("entriesTotal").GetInt32()).IsEqualTo(5);
        var table = AgentClassificationReader.Read(Doc);
        await Assert.That(table.Find("10.10")!.Description).IsEqualTo("Walls");
        await Assert.That(result.Warnings.Any(warning => warning.Contains("10.10") && warning.Contains("DuplicateEntry"))).IsTrue();
    }

    [Test]
    public async Task Load_DoesNotUndoTheTableOfADocumentThatAlreadyHasOne()
    {
        var first = Load(SampleFile("keep-a.txt"), dryRun: false);
        await Assert.That(first.Success).IsTrue();

        // A dry run of another table rolls back with its transaction.
        var dryRun = Load(SampleFile("keep-b.txt", ClassificationFixtures.RenamedTable), dryRun: true);
        await Assert.That(dryRun.Success).IsTrue();
        await Assert.That(AgentClassificationReader.Read(Doc).Find("10.10")!.Description).IsEqualTo("Walls");

        // A refused file leaves the table untouched as well.
        var refused = Load(SampleFile("keep-c.txt", ClassificationFixtures.ThreeColumnTable), dryRun: false);
        await Assert.That(refused.Success).IsFalse();
        await Assert.That(AgentClassificationReader.Read(Doc).Find("10.10")!.Description).IsEqualTo("Walls");
    }

    [Test]
    public async Task Overview_WithTable_ReportsLoadedEntriesAndSource()
    {
        var path = SampleFile("overview.txt");
        await Assert.That(Load(path, dryRun: false).Success).IsTrue();

        var result = new GetProjectOverviewTool(new StubRevitContext(Doc))
            .ExecuteForTests(Doc, Args(new { }), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var classification = result.Data!.Value.GetProperty("classification");
        await Assert.That(classification.GetProperty("loaded").GetBoolean()).IsTrue();
        await Assert.That(classification.GetProperty("entries").GetInt32()).IsEqualTo(11);
        await Assert.That(classification.GetProperty("source").GetString()).IsEqualTo(path);
    }

    // ── list_classification_entries ──────────────────────────────────────

    [Test]
    public async Task List_Default_ReturnsLeafCodesWithBreadcrumbs()
    {
        var path = SampleFile("list-default.txt");
        await Assert.That(Load(path, dryRun: false).Success).IsTrue();

        var result = List(new { });

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("loaded").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("source").GetString()).IsEqualTo(path);
        await Assert.That(data.GetProperty("entriesTotal").GetInt32()).IsEqualTo(11);
        await Assert.That(data.GetProperty("leafTotal").GetInt32()).IsEqualTo(6);
        await Assert.That(data.GetProperty("returned").GetInt32()).IsEqualTo(6);
        await Assert.That(data.GetProperty("truncated").GetBoolean()).IsFalse();
        await Assert.That(EntryCodes(result)).IsEqualTo("10.10.10,10.10.20,20.10.10,20.20.10,20.20.20,30");

        var first = data.GetProperty("entries")[0];
        await Assert.That(first.GetProperty("code").GetString()).IsEqualTo("10.10.10");
        await Assert.That(first.GetProperty("breadcrumb").GetString()).IsEqualTo("Shell › Walls › Load-bearing walls");
        await Assert.That(string.Join(",", first.EnumerateObject().Select(property => property.Name)))
            .IsEqualTo("code,breadcrumb");
    }

    [Test]
    public async Task List_AllFields_DescribeTheRow()
    {
        await Assert.That(Load(SampleFile("list-fields.txt"), dryRun: false).Success).IsTrue();

        var result = List(new
        {
            fields = new[] { "code", "description", "level", "parentCode", "breadcrumb", "categoryId", "isLeaf" },
            leafOnly = false,
            contains = "Partitions"
        });

        await Assert.That(result.Success).IsTrue();
        var entry = result.Data!.Value.GetProperty("entries")[0];
        await Assert.That(entry.GetProperty("description").GetString()).IsEqualTo("Partitions");
        await Assert.That(entry.GetProperty("level").GetInt32()).IsEqualTo(3);
        await Assert.That(entry.GetProperty("parentCode").GetString()).IsEqualTo("10.10");
        await Assert.That(entry.GetProperty("breadcrumb").GetString()).IsEqualTo("Shell › Walls › Partitions");
        await Assert.That(entry.GetProperty("isLeaf").GetBoolean()).IsTrue();
        await Assert.That(entry.TryGetProperty("categoryId", out _)).IsFalse();
    }

    [Test]
    public async Task List_Contains_MatchesCodeAndDescription()
    {
        await Assert.That(Load(SampleFile("list-contains.txt"), dryRun: false).Success).IsTrue();

        var byName = List(new { contains = "VALVE" });
        await Assert.That(EntryCodes(byName)).IsEqualTo("20.20.10,20.20.20");

        // A substring of the code, wherever it sits in the code.
        var byCode = List(new { contains = "10.10" });
        await Assert.That(EntryCodes(byCode)).IsEqualTo("10.10.10,10.10.20,20.10.10");

        var byBranchName = List(new { contains = "Pipelines", leafOnly = false });
        await Assert.That(EntryCodes(byBranchName)).IsEqualTo("20.10,20.10.10");
    }

    [Test]
    public async Task List_ParentCode_ReturnsTheBranchWithItsRoot()
    {
        await Assert.That(Load(SampleFile("list-parent.txt"), dryRun: false).Success).IsTrue();

        var result = List(new { parentCode = "20.10", leafOnly = false });

        await Assert.That(EntryCodes(result)).IsEqualTo("20.10,20.10.10");

        var leavesOnly = List(new { parentCode = "10" });
        await Assert.That(EntryCodes(leavesOnly)).IsEqualTo("10.10.10,10.10.20");
    }

    [Test]
    public async Task List_Level_FiltersTheTreeLevel()
    {
        await Assert.That(Load(SampleFile("list-level.txt"), dryRun: false).Success).IsTrue();

        var roots = List(new { level = 1, leafOnly = false });
        await Assert.That(EntryCodes(roots)).IsEqualTo("10,20,30");

        var branches = List(new { level = 2, leafOnly = false });
        await Assert.That(EntryCodes(branches)).IsEqualTo("10.10,20.10,20.20");

        var outOfRange = List(new { level = 7 });
        await Assert.That(outOfRange.Success).IsFalse();
        await Assert.That(outOfRange.ErrorCode).IsEqualTo("invalid_arguments");
    }

    [Test]
    public async Task List_PagesThroughTheTable()
    {
        await Assert.That(Load(SampleFile("list-paging.txt"), dryRun: false).Success).IsTrue();

        var first = List(new { limit = 2 });
        await Assert.That(EntryCodes(first)).IsEqualTo("10.10.10,10.10.20");
        var firstPage = first.Data!.Value;
        await Assert.That(firstPage.GetProperty("returned").GetInt32()).IsEqualTo(2);
        await Assert.That(firstPage.GetProperty("offset").GetInt32()).IsEqualTo(0);
        await Assert.That(firstPage.GetProperty("nextOffset").GetInt32()).IsEqualTo(2);
        await Assert.That(firstPage.GetProperty("truncated").GetBoolean()).IsTrue();
        await Assert.That(firstPage.GetProperty("hint").GetString()).Contains("parentCode");
        await Assert.That(first.Message).Contains("Narrow with");

        var middle = List(new { limit = 2, offset = 2 });
        await Assert.That(EntryCodes(middle)).IsEqualTo("20.10.10,20.20.10");
        await Assert.That(middle.Data!.Value.GetProperty("nextOffset").GetInt32()).IsEqualTo(4);

        var last = List(new { limit = 2, offset = 4 });
        await Assert.That(EntryCodes(last)).IsEqualTo("20.20.20,30");
        await Assert.That(last.Data!.Value.GetProperty("truncated").GetBoolean()).IsFalse();

        var beyond = List(new { limit = 2, offset = 6 });
        await Assert.That(beyond.Success).IsTrue();
        await Assert.That(EntryCodes(beyond)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task List_NoMatch_ReturnsAnEmptyPageAndHowToStartOver()
    {
        await Assert.That(Load(SampleFile("list-empty.txt"), dryRun: false).Success).IsTrue();

        var result = List(new { contains = "zzz" });

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("returned").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("entriesTotal").GetInt32()).IsEqualTo(11);
        await Assert.That(data.GetProperty("entries").GetArrayLength()).IsEqualTo(0);
        await Assert.That(data.GetProperty("hint").GetString()).Contains("level:1, leafOnly:false");

        var unknownBranch = List(new { parentCode = "99.99" });
        await Assert.That(unknownBranch.Data!.Value.GetProperty("hint").GetString()).Contains("'99.99' is not a code of this table");
    }

    [Test]
    public async Task List_UnknownField_IsInvalidArguments()
    {
        await Assert.That(Load(SampleFile("list-unknown-field.txt"), dryRun: false).Success).IsTrue();

        var result = List(new { fields = new[] { "code", "title" } });

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_arguments");
        await Assert.That(result.Message).Contains("title");
        await Assert.That(result.Message).Contains("Allowed fields: code, description, level, parentCode, breadcrumb, categoryId, isLeaf");
    }

    [Test]
    public async Task List_LeavesTheDocumentUnchanged()
    {
        await Assert.That(Load(SampleFile("list-read-only.txt"), dryRun: false).Success).IsTrue();
        var modified = Doc.IsModified;

        var result = List(new { leafOnly = false, limit = 500 });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("entries").GetArrayLength()).IsEqualTo(11);
        await Assert.That(Doc.IsModified).IsEqualTo(modified);
    }
}

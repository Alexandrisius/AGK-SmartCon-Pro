using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

public sealed class AgentSheetScheduleToolsTests : RevitApiTest
{
    private const string LevelName = "SC_SheetLevel";
    private const string PlanName = "SC_SheetPlan";
    private const string ScheduleName = "SC_Agent_Schedule";
    private const string SheetNumber = "SC-A101";
    private const string SheetName = "SC_Agent_Sheet";
    private const string WallComment = "SC_Agent_Wall";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private long _scheduleId;
    private long _sheetId;
    private long _viewId;
    private bool _sheetCreated;
    private bool _scheduleCreated;
    private int _wallCount;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _transactions.RunInTransaction(Doc, "Seed sheet and schedule model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            level.Name = LevelName;
            var plan = ModelSeed.CreateViewPlanChecked(doc, level.Id, PlanName);
            _viewId = plan.Id.GetValue();

            for (var index = 0; index < 3; index++)
            {
                var wall = ModelSeed.TryCreateWallAt(doc, level.Id, index * 3.0);
                if (wall is null)
                    continue;

                wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                    ?.Set(WallComment);
                _wallCount++;
            }

            var schedule = ModelSeed.CreateScheduleChecked(doc);
            schedule.Name = ScheduleName;
            schedule.Definition.AddField(
                ScheduleFieldType.Instance,
                new ElementId(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS));
            _scheduleId = schedule.Id.GetValue();
            _scheduleCreated = true;

            var titleBlockId = ModelSeed.FindTitleBlockTypeId(doc);
            if (titleBlockId is null)
            {
                var titleBlockPath = ModelSeed.FindTitleBlockFamilyFile(
                    doc.Application.VersionNumber);
                if (titleBlockPath is not null)
                {
                    doc.LoadFamily(titleBlockPath, out _);
                    titleBlockId = ModelSeed.FindTitleBlockTypeId(doc);
                }
            }

            if (titleBlockId is null)
                return;

            var sheet = ViewSheet.Create(doc, titleBlockId);
            sheet.SheetNumber = SheetNumber;
            sheet.Name = SheetName;
            _sheetId = sheet.Id.GetValue();
            Viewport.Create(doc, sheet.Id, plan.Id, new XYZ(0.25, 0.25, 0));
            ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, new XYZ(0.25, 0.1, 0));
            _sheetCreated = true;
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task InvisibleSheetNumber_ListAndLookupReturnCandidates()
    {
        if (!_sheetCreated)
            Skip.Test("Лист не создался в шаблоне проекта");

        var titleBlockId = ModelSeed.FindTitleBlockTypeId(Doc);
        if (titleBlockId is null)
            Skip.Test("Основная надпись недоступна");

        var hiddenNumber = SheetNumber + "\u200B";
        try
        {
            _transactions!.RunInTransaction(Doc, "Seed invisible sheet number", doc =>
            {
                var sheet = ViewSheet.Create(doc, titleBlockId);
                sheet.SheetNumber = hiddenNumber;
                sheet.Name = "SC_Agent_Sheet_Invisible";
            });
        }
        catch (Exception)
        {
            Skip.Test("Revit не разрешает номер листа с невидимым символом");
        }

        var list = new ListSheetsTool(new StubRevitContext(Doc));
        var listResult = list.ExecuteForTests(
            Doc,
            Parse("{\"numberContains\":\"" + SheetNumber + "\"}"),
            CancellationToken.None);

        await Assert.That(listResult.Success).IsTrue();
        var cards = listResult.Data!.Value.GetProperty("sheets").EnumerateArray().ToList();
        var invisible = cards.FirstOrDefault(card =>
            card.GetProperty("hasInvisibleCharacters").GetBoolean());
        await Assert.That(invisible.ValueKind).IsNotEqualTo(JsonValueKind.Undefined);
        await Assert.That(invisible.GetProperty("numberVisual").GetString())
            .IsEqualTo(SheetNumber);
        await Assert.That(invisible.GetProperty("numberCodePoints").GetArrayLength())
            .IsGreaterThan(0);
        await Assert.That(
                listResult.Data.Value.GetProperty("visualDuplicates").GetArrayLength())
            .IsGreaterThanOrEqualTo(1);

        var content = new GetSheetContentTool(new StubRevitContext(Doc));
        var contentResult = content.ExecuteForTests(
            Doc,
            Parse("{\"number\":\"" + SheetNumber + "\"}"),
            CancellationToken.None);

        await Assert.That(contentResult.Success).IsFalse();
        await Assert.That(contentResult.ErrorCode).IsEqualTo("ambiguous_sheet");
        await Assert.That(
                contentResult.Data!.Value.GetProperty("candidates").GetArrayLength())
            .IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task ListSchedules_ReturnsSeededScheduleWithFieldsAndRows()
    {
        if (!_scheduleCreated)
            Skip.Test("Ведомость не создалась в шаблоне проекта");

        var tool = new ListSchedulesTool(new StubRevitContext(Doc));
        var args = Parse("{\"nameContains\":\"SC_Agent\"}");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("total").GetInt32()).IsGreaterThanOrEqualTo(1);
        var schedule = data.GetProperty("schedules").EnumerateArray()
            .First(item => item.GetProperty("scheduleId").GetInt64() == _scheduleId);
        await Assert.That(schedule.GetProperty("category").GetString()).IsEqualTo("OST_Walls");
        await Assert.That(schedule.GetProperty("fieldCount").GetInt32()).IsGreaterThanOrEqualTo(1);
        await Assert.That(schedule.GetProperty("rowCount").GetInt32())
            .IsEqualTo(Math.Max(1, _wallCount));
        await Assert.That(schedule.GetProperty("isTemplate").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task GetScheduleData_ReturnsRowsAndHonoursPaging()
    {
        if (!_scheduleCreated)
            Skip.Test("Ведомость не создалась в шаблоне проекта");

        var tool = new GetScheduleDataTool(new StubRevitContext(Doc));
        var page = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + _scheduleId + ",\"limit\":2}"),
            CancellationToken.None);

        await Assert.That(page.Success).IsTrue();
        var data = page.Data!.Value;
        await Assert.That(data.GetProperty("columnCount").GetInt32()).IsGreaterThanOrEqualTo(1);
        await Assert.That(data.GetProperty("columns").GetArrayLength())
            .IsEqualTo(data.GetProperty("columnCount").GetInt32());
        await Assert.That(data.GetProperty("returned").GetInt32()).IsLessThanOrEqualTo(2);
        if (data.GetProperty("totalRows").GetInt32() > 2)
        {
            await Assert.That(data.GetProperty("truncated").GetBoolean()).IsTrue();
            await Assert.That(data.GetProperty("nextOffset").GetInt32()).IsEqualTo(2);
        }

        var second = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + _scheduleId + ",\"limit\":2,\"offset\":2}"),
            CancellationToken.None);
        await Assert.That(second.Success).IsTrue();
        await Assert.That(second.Data!.Value.GetProperty("offset").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task GetScheduleData_ByName_AndUnknownNameFails()
    {
        if (!_scheduleCreated)
            Skip.Test("Ведомость не создалась в шаблоне проекта");

        var tool = new GetScheduleDataTool(new StubRevitContext(Doc));
        var found = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleName\":\"" + ScheduleName + "\",\"limit\":1}"),
            CancellationToken.None);
        var missing = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleName\":\"SC_Missing_Schedule\"}"),
            CancellationToken.None);

        await Assert.That(found.Success).IsTrue();
        await Assert.That(found.Data!.Value.GetProperty("name").GetString())
            .IsEqualTo(ScheduleName);
        await Assert.That(missing.Success).IsFalse();
        await Assert.That(missing.ErrorCode).IsEqualTo("schedule_not_found");
    }

    [Test]
    public async Task ListSheets_ReturnsSeededSheetWithSizeAndCounts()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var tool = new ListSheetsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(Doc, Parse("{\"numberContains\":\"SC-A\"}"), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        var sheet = data.GetProperty("sheets").EnumerateArray()
            .First(item => item.GetProperty("sheetId").GetInt64() == _sheetId);
        await Assert.That(sheet.GetProperty("number").GetString()).IsEqualTo(SheetNumber);
        await Assert.That(sheet.GetProperty("name").GetString()).IsEqualTo(SheetName);
        await Assert.That(sheet.GetProperty("widthMm").GetDouble()).IsGreaterThan(0);
        await Assert.That(sheet.GetProperty("heightMm").GetDouble()).IsGreaterThan(0);
        await Assert.That(sheet.GetProperty("viewCount").GetInt32()).IsGreaterThanOrEqualTo(1);
        await Assert.That(sheet.GetProperty("scheduleCount").GetInt32()).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task ListSheets_OffsetBeyondTotal_ReturnsEmptyPageWithNextOffset()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var tool = new ListSheetsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"offset\":500,\"limit\":10}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("returned").GetInt32()).IsEqualTo(0);
        await Assert.That(result.Data.Value.GetProperty("truncated").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task GetSheetContent_ReturnsViewportAndScheduleCoordinates()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var tool = new GetSheetContentTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"number\":\"" + SheetNumber + "\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("sheet").GetProperty("number").GetString())
            .IsEqualTo(SheetNumber);
        await Assert.That(data.GetProperty("placedViewCount").GetInt32())
            .IsGreaterThanOrEqualTo(1);

        var viewport = data.GetProperty("views").EnumerateArray()
            .First(item => item.GetProperty("viewId").GetInt64() == _viewId);
        await Assert.That(viewport.GetProperty("name").GetString()).IsEqualTo(PlanName);
        await Assert.That(viewport.GetProperty("centerXMm").ValueKind)
            .IsEqualTo(JsonValueKind.Number);
        await Assert.That(viewport.GetProperty("boxWidthMm").GetDouble()).IsGreaterThan(0);

        await Assert.That(data.GetProperty("placedScheduleCount").GetInt32())
            .IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task GetSheetContent_UnknownNumber_ReturnsStructuredFailure()
    {
        var tool = new GetSheetContentTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"number\":\"ZZ-999\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("sheet_not_found");
    }

    [Test]
    public async Task GetSheetContent_WithoutKey_ReturnsInvalidArguments()
    {
        var tool = new GetSheetContentTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_arguments");
    }

    [Test]
    public async Task ListSchedules_UnknownCategory_IsReportedNotDropped()
    {
        var tool = new ListSchedulesTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"categories\":[\"OST_NoSuchCategory\"]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var unknown = result.Data!.Value.GetProperty("unknownCategories");
        await Assert.That(unknown.ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(unknown.GetArrayLength()).IsEqualTo(1);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
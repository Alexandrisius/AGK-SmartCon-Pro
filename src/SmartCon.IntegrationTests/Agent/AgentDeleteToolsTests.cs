using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// G.1/S.5: delete_elements and delete_schedules integration coverage — the
/// dry-run name list, the confirmNames gate (required / mismatch / stale
/// guard) and the retrofit refusal of the pre-existing delete tools.
/// </summary>
public sealed class AgentDeleteToolsTests : RevitApiTest
{
    private const string LevelName = "SC_Delete_Level";
    private const string PlanName = "SC_Delete_Deps_Plan";
    private const string ScheduleName = "SC_Delete_Schedule";
    private const string SheetNumber = "SC-DEL-100";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private long _wallId;
    private long _levelId;
    private long _planId;
    private long _scheduleId;
    private bool _wallCreated;
    private bool _scheduleCreated;
    private bool _sheetCreated;
    private long _sheetId;

    private Document Doc => _document!;

    private string WallName => Doc.GetElement(ElementIdCompat.Create(_wallId))!.Name;

    private string LevelNameOf =>
        ((Level)Doc.GetElement(ElementIdCompat.Create(_levelId))!).Name;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed delete tools model", doc =>
        {
            var wallLevel = ModelSeed.CreateLevel(doc, 0.0);
            var wall = ModelSeed.TryCreateWallAt(doc, wallLevel.Id, 0.0);
            if (wall is not null)
            {
                _wallId = wall.Id.GetValue();
                _wallCreated = true;
            }

            var level = ModelSeed.CreateLevel(doc, 30.0);
            level.Name = LevelName;
            _levelId = level.Id.GetValue();

            var plan = ModelSeed.CreateViewPlanChecked(doc, level.Id, PlanName);
            _planId = plan.Id.GetValue();

            var schedule = ModelSeed.CreateScheduleChecked(doc);
            schedule.Name = ScheduleName;
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
            sheet.Name = "SC_Delete_Sheet";
            _sheetId = sheet.Id.GetValue();
            _sheetCreated = true;
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task DeleteElements_DryRun_ListsNamesAndDependents_NoChanges()
    {
        GuardWall();

        var tool = CreateElementsTool();
        var result = tool.ExecuteForTests(
            Parse("{\"elementIds\":[" + _wallId + "," + _levelId + "],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var names = ReadStringArray(result.Data!.Value, "names");
        await Assert.That(names).Contains(LevelNameOf);
        await Assert.That(names).Contains(WallName);

        var items = result.Data!.Value.GetProperty("items");
        await Assert.That(items.GetArrayLength()).IsEqualTo(2);
        foreach (var item in items.EnumerateArray())
        {
            if (item.GetProperty("elementId").GetInt64() == _levelId)
            {
                await Assert.That(item.GetProperty("name").GetString()).IsEqualTo(LevelNameOf);
                await Assert.That(item.GetProperty("dependentsCount").GetInt32()).IsGreaterThanOrEqualTo(1);
            }
        }

        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_wallId))).IsNotNull();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_levelId))).IsNotNull();
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeleteElements_Apply_WithoutConfirmNames_Refused()
    {
        GuardWall();

        var tool = CreateElementsTool();
        var result = tool.ExecuteForTests(
            Parse("{\"elementIds\":[" + _wallId + "],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("confirm_names_required");
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_wallId))).IsNotNull();
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeleteElements_Apply_WrongConfirmNames_Refused()
    {
        GuardWall();

        var tool = CreateElementsTool();
        var result = tool.ExecuteForTests(
            Parse("{\"elementIds\":[" + _wallId + "],\"confirmNames\":[\"Нет такого имени\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("confirm_names_mismatch");
        var missing = ReadStringArray(result.Data!.Value, "missing");
        await Assert.That(missing).Contains(WallName);
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_wallId))).IsNotNull();
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeleteElements_Apply_FreshConfirmNames_DeletesWithDependents()
    {
        GuardWall();

        var tool = CreateElementsTool();
        var result = tool.ExecuteForTests(
            Parse("{\"elementIds\":[" + _wallId + "," + _levelId +
                  "],\"confirmNames\":[\"" + WallName + "\",\"" + LevelNameOf +
                  "\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_wallId))).IsNull();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_levelId))).IsNull();
        // The plan was a dependent of the deleted level and went with it.
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_planId))).IsNull();
        await Assert.That(result.Data!.Value.GetProperty("deletedCount").GetInt32())
            .IsEqualTo(2);
        await Assert.That(result.Data!.Value.GetProperty("dependentsDeleted").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        await Assert.That(result.Data!.Value.GetProperty("undoSupported").GetBoolean())
            .IsFalse();
    }

    [Test]
    public async Task DeleteElements_Apply_StaleConfirmNames_Refused()
    {
        GuardWall();

        var tool = CreateElementsTool();
        var dryRun = tool.ExecuteForTests(
            Parse("{\"elementIds\":[" + _wallId + "],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(dryRun.Success).IsTrue();

        // The plan is a new target that was not in the confirmed dry run: the
        // fresh name set differs from the confirmed one and the apply must stop.
        var result = tool.ExecuteForTests(
            Parse("{\"elementIds\":[" + _wallId + "," + _planId +
                  "],\"confirmNames\":[\"" + WallName + "\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("confirm_names_mismatch");
        var missing = ReadStringArray(result.Data!.Value, "missing");
        await Assert.That(missing).Contains(PlanName);
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_wallId))).IsNotNull();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_planId))).IsNotNull();
    }

    [Test]
    public async Task DeleteSchedules_DryRunThenConfirmGateThenApply()
    {
        GuardSchedule();

        var tool = CreateSchedulesTool();

        var dryRun = tool.ExecuteForTests(
            Parse("{\"scheduleIds\":[" + _scheduleId + "],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(dryRun.Success).IsTrue();
        var names = ReadStringArray(dryRun.Data!.Value, "names");
        await Assert.That(names).Contains(ScheduleName);
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_scheduleId))).IsNotNull();

        var noConfirm = tool.ExecuteForTests(
            Parse("{\"scheduleIds\":[" + _scheduleId + "],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(noConfirm.Success).IsFalse();
        await Assert.That(noConfirm.ErrorCode).IsEqualTo("confirm_names_required");
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_scheduleId))).IsNotNull();

        var apply = tool.ExecuteForTests(
            Parse("{\"scheduleIds\":[" + _scheduleId + "],\"confirmNames\":[\"" +
                  ScheduleName + "\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(apply.Success).IsTrue();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_scheduleId))).IsNull();
        await Assert.That(apply.Data!.Value.GetProperty("deletedCount").GetInt32())
            .IsEqualTo(1);
    }

    [Test]
    public async Task DeleteSchedules_ByName_ResolvesAndApplies()
    {
        GuardSchedule();

        var tool = CreateSchedulesTool();
        var result = tool.ExecuteForTests(
            Parse("{\"scheduleNames\":[\"" + ScheduleName + "\"],\"confirmNames\":[\"" +
                  ScheduleName + "\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_scheduleId))).IsNull();
    }

    [Test]
    public async Task DeleteSchedules_TitleblockRevisionSchedule_NotDeletable()
    {
        ViewSchedule? revision = null;
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(ViewSchedule)))
        {
            if (element is ViewSchedule schedule && schedule.IsTitleblockRevisionSchedule)
            {
                revision = schedule;
                break;
            }
        }

        if (revision is null)
        {
            Skip.Test(
                "В модели без размещённых титблоков нет titleblock revision schedules — " +
                "они создаются Revit вместе со штампом (см. AgentSheetReader.CountPlacedSchedules)");
            return;
        }

        var tool = CreateSchedulesTool();
        var result = tool.ExecuteForTests(
            Parse("{\"scheduleIds\":[" + revision.Id.GetValue() + "],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        var failure = result.Data!.Value.GetProperty("failed").EnumerateArray().First();
        await Assert.That(failure.GetProperty("errorCode").GetString())
            .IsEqualTo("schedule_not_deletable");
        await Assert.That(Doc.GetElement(revision.Id)).IsNotNull();
    }

    [Test]
    public async Task DeleteViews_OnSchedule_RedirectsToDeleteSchedules()
    {
        GuardSchedule();

        var tool = new DeleteViewsTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"viewIds\":[" + _scheduleId + "],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        var failure = result.Data!.Value.GetProperty("failed").EnumerateArray().First();
        await Assert.That(failure.GetProperty("errorCode").GetString())
            .IsEqualTo("use_delete_schedules");
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_scheduleId))).IsNotNull();
    }

    [Test]
    public async Task DeleteSheets_Apply_WithoutConfirmNames_Refused()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var tool = new DeleteSheetsTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"sheetIds\":[" + _sheetId + "],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("confirm_names_required");
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_sheetId))).IsNotNull();
    }

    [Test]
    public async Task DeleteSheets_DryRun_PublishesNames()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var tool = new DeleteSheetsTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"sheetIds\":[" + _sheetId + "],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var names = ReadStringArray(result.Data!.Value, "names");
        await Assert.That(names.Count).IsEqualTo(1);
        await Assert.That(names[0]).Contains(SheetNumber);
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_sheetId))).IsNotNull();
    }

    private DeleteElementsTool CreateElementsTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private DeleteSchedulesTool CreateSchedulesTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private void GuardWall()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта нет WallType — стену не создать");
    }

    private void GuardSchedule()
    {
        if (!_scheduleCreated)
            Skip.Test("Не удалось создать ViewSchedule в шаблоне проекта");
    }

    private static List<string> ReadStringArray(JsonElement data, string property)
    {
        var values = new List<string>();
        foreach (var item in data.GetProperty(property).EnumerateArray())
            values.Add(item.GetString() ?? string.Empty);
        return values;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

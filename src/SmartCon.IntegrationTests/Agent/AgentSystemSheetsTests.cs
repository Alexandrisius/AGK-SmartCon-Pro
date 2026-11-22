using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Wave-3 block 8 tools: get_titleblock_options, get_view_placement_preview
/// and the build_system_sheets composite (case 6 v2 — sheets for per-system
/// 3D views with the viewport inside the border, the schedule to the right
/// and album-number continuation). The seed is an album of three sheets 1..3
/// with a loaded title block, two plan views and a walls schedule.
/// </summary>
public sealed class AgentSystemSheetsTests : RevitApiTest
{
    private const string PlanAName = "SC_SysSheets_Plan_A";
    private const string PlanBName = "SC_SysSheets_Plan_B";
    private const string ScheduleName = "SC_SysSheets_Спека";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private bool _albumCreated;
    private long _planAId;
    private long _planBId;
    private long _scheduleId;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed system sheets model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 0.0);
            var planA = ModelSeed.CreateViewPlanChecked(doc, level.Id, PlanAName);
            _planAId = planA.Id.GetValue();
            var planB = ModelSeed.CreateViewPlanChecked(doc, level.Id, PlanBName);
            _planBId = planB.Id.GetValue();
            try
            {
                // The preview test relies on plan B having no active crop; some
                // project templates enable the crop by default.
                planB.CropBoxActive = false;
            }
            catch (Exception)
            {
            }

            // Viewport.Create returns null on a document that was never
            // regenerated with model geometry (IT-verified on Revit 2025).
            ModelSeed.TryCreateWallAt(doc, level.Id, 0.0);

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

            var albumNames = new[] { "Общие данные", "План 1 этажа", "План 2 этажа" };
            for (var index = 0; index < albumNames.Length; index++)
            {
                var sheet = ViewSheet.Create(doc, titleBlockId);
                sheet.SheetNumber = (index + 1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                sheet.Name = albumNames[index];
            }

            _albumCreated = true;

            var schedule = ModelSeed.CreateScheduleChecked(doc);
            schedule.Name = ScheduleName;
            _scheduleId = schedule.Id.GetValue();
            var markField = FindSchedulableMark(schedule.Definition);
            if (markField is not null)
                schedule.Definition.AddField(markField);
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task BuildSystemSheets_TwoItems_ContinuesAlbumAndPlacesInside()
    {
        if (!_albumCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var tool = new BuildSystemSheetsTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"items\":[" +
                  "{\"viewId\":" + _planAId + ",\"sheetName\":\"Схема Т1\"}," +
                  "{\"viewId\":" + _planBId + ",\"scheduleId\":" + _scheduleId +
                  ",\"sheetName\":\"Схема Т2\"}]," +
                  "\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var sheetFour = FindSheet("4");
        var sheetFive = FindSheet("5");
        await Assert.That(sheetFour).IsNotNull();
        await Assert.That(sheetFive).IsNotNull();
        await Assert.That(sheetFour!.Name).IsEqualTo("Схема Т1");
        await Assert.That(sheetFive!.Name).IsEqualTo("Схема Т2");

        await AssertViewportInsideSheet(sheetFour, 1.0);
        var rows = result.Data!.Value.GetProperty("items").EnumerateArray().ToList();
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0].GetProperty("status").GetString()).IsEqualTo("built");
        await Assert.That(rows[1].GetProperty("status").GetString()).IsEqualTo("built");
        await Assert.That(rows[0].GetProperty("insideSheet").GetBoolean()).IsTrue();
        await Assert.That(rows[1].GetProperty("insideSheet").GetBoolean()).IsTrue();

        // The schedule on sheet 5 sits to the RIGHT of the viewport box.
        var scheduleInstance = FindScheduleOnSheet(sheetFive!);
        await Assert.That(scheduleInstance).IsNotNull();
        var viewportBox = GetOnlyViewportBox(sheetFive!);
        await Assert.That(
            FeetToMm(scheduleInstance!.Point.X) >
            FeetToMm(viewportBox.MaximumPoint.X) - 0.5).IsTrue();

        var set = _changeSets!.Last!;
        await Assert.That(set.Header.Operation).IsEqualTo("build_system_sheets");
        var keys = set.Items.Select(item => item.ParameterName).ToList();
        await Assert.That(keys.Count(item => item == "sheet_created")).IsEqualTo(2);
        await Assert.That(keys.Count(item => item == "viewport_created")).IsEqualTo(2);
        await Assert.That(keys.Count(item => item == "schedule_placed")).IsEqualTo(1);
    }

    [Test]
    public async Task BuildSystemSheets_TakenNumber_FailsItemOthersProceed()
    {
        if (!_albumCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var tool = new BuildSystemSheetsTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"items\":[" +
                  "{\"viewId\":" + _planAId + ",\"sheetName\":\"Занятый номер\",\"sheetNumber\":\"1\"}," +
                  "{\"viewId\":" + _planBId + ",\"sheetName\":\"Хороший лист\"}]," +
                  "\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var rows = result.Data!.Value.GetProperty("items").EnumerateArray().ToList();
        await Assert.That(rows[0].GetProperty("status").GetString()).IsEqualTo("failed");
        await Assert.That(rows[0].GetProperty("errorCode").GetString())
            .IsEqualTo("sheet_number_taken");
        await Assert.That(rows[1].GetProperty("status").GetString()).IsEqualTo("built");
        await Assert.That(FindSheet("4")).IsNotNull();
        await Assert.That(FindSheet("1")!.Name).IsEqualTo("Общие данные");
    }

    [Test]
    public async Task BuildSystemSheets_UnknownTitleBlockParameter_WarnsSheetStays()
    {
        if (!_albumCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var tool = new BuildSystemSheetsTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"items\":[{\"viewId\":" + _planAId +
                  ",\"sheetName\":\"Схема Т1\",\"sheetNumber\":\"SC-9\"," +
                  "\"titleBlockParameters\":{\"SC_НетТакогоПараметра\":2}}]," +
                  "\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var sheet = FindSheet("SC-9");
        await Assert.That(sheet).IsNotNull();
        var rows = result.Data!.Value.GetProperty("items").EnumerateArray().ToList();
        var parameters = rows[0].GetProperty("titleBlockParameters").EnumerateArray().ToList();
        await Assert.That(parameters.Count).IsEqualTo(1);
        await Assert.That(parameters[0].GetProperty("status").GetString()).IsEqualTo("failed");
        await Assert.That(parameters[0].GetProperty("errorCode").GetString())
            .IsEqualTo("titleblock_parameter_not_found");
    }

    [Test]
    public async Task BuildSystemSheets_UndoLast_RemovesWholeBatch()
    {
        if (!_albumCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var tool = new BuildSystemSheetsTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var built = tool.ExecuteForTests(
            Parse("{\"items\":[" +
                  "{\"viewId\":" + _planAId + ",\"sheetName\":\"Схема Т1\"}," +
                  "{\"viewId\":" + _planBId + ",\"scheduleId\":" + _scheduleId +
                  ",\"sheetName\":\"Схема Т2\"}]," +
                  "\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(built.Success).IsTrue();
        await Assert.That(FindSheet("4")).IsNotNull();
        await Assert.That(FindSheet("5")).IsNotNull();

        var undoTool = new UndoLastTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undo = undoTool.ExecuteForTests(
            Parse("""{"dryRun":false}"""),
            approved: true,
            CancellationToken.None);

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(FindSheet("4")).IsNull();
        await Assert.That(FindSheet("5")).IsNull();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_planAId))).IsNotNull();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_scheduleId))).IsNotNull();
    }

    [Test]
    public async Task GetTitleblockOptions_ListsTypesAlbumAndHint()
    {
        if (!_albumCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var tool = new GetTitleblockOptionsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc, Parse("""{}"""), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var titleblocks = result.Data!.Value.GetProperty("titleblocks").EnumerateArray().ToList();
        await Assert.That(titleblocks.Count >= 1).IsTrue();
        await Assert.That(titleblocks[0].GetProperty("familyName").GetString()).IsNotNull();
        await Assert.That(titleblocks[0].GetProperty("typeName").GetString()).IsNotNull();
        await Assert.That(titleblocks[0].TryGetProperty("typeParameters", out _)).IsTrue();

        var sheets = result.Data.Value.GetProperty("existingSheets").EnumerateArray().ToList();
        await Assert.That(sheets.Count).IsEqualTo(3);
        await Assert.That(result.Data.Value.GetProperty("nextNumberHint").GetString())
            .IsEqualTo("4");
    }

    [Test]
    public async Task GetViewPlacementPreview_PlanGivesEstimate()
    {
        _transactions!.RunInTransaction(Doc, "Enable crop on plan A", doc =>
        {
            var plan = (ViewPlan)Doc.GetElement(ElementIdCompat.Create(_planAId))!;
            var box = plan.CropBox;
            box.Min = new XYZ(-10, -8, box.Min.Z);
            box.Max = new XYZ(10, 8, box.Max.Z);
            plan.CropBox = box;
            doc.Regenerate();
            plan.CropBoxActive = true;
        });

        var tool = new GetViewPlacementPreviewTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"viewIds\":[" + _planAId + "," + _planBId + "]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var views = result.Data!.Value.GetProperty("views").EnumerateArray().ToList();
        await Assert.That(views.Count).IsEqualTo(2);
        await Assert.That(views[0].GetProperty("estimated").GetBoolean()).IsTrue();
        await Assert.That(views[0].GetProperty("paperWidthMm").GetDouble() > 0).IsTrue();
        await Assert.That(views[0].GetProperty("paperHeightMm").GetDouble() > 0).IsTrue();
        await Assert.That(views[0].GetProperty("kind").GetString()).IsEqualTo("plan");

        // A plan without an active crop is honestly not estimable.
        await Assert.That(views[1].GetProperty("estimated").GetBoolean()).IsFalse();
    }

    private ViewSheet? FindSheet(string number)
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(ViewSheet)))
        {
            if (element is ViewSheet sheet &&
                string.Equals(sheet.SheetNumber, number, StringComparison.Ordinal))
                return sheet;
        }

        return null;
    }

    private async Task AssertViewportInsideSheet(ViewSheet sheet, double toleranceMm)
    {
        var outline = sheet.Outline;
        foreach (var viewportId in sheet.GetAllViewports())
        {
            var viewport = (Viewport)Doc.GetElement(viewportId)!;
            var box = viewport.GetBoxOutline();
            var leftMm = FeetToMm(box.MinimumPoint.X);
            var bottomMm = FeetToMm(box.MinimumPoint.Y);
            var rightMm = FeetToMm(box.MaximumPoint.X);
            var topMm = FeetToMm(box.MaximumPoint.Y);
            await Assert.That(
                leftMm >= FeetToMm(outline.Min.U) - toleranceMm).IsTrue();
            await Assert.That(
                bottomMm >= FeetToMm(outline.Min.V) - toleranceMm).IsTrue();
            await Assert.That(
                rightMm <= FeetToMm(outline.Max.U) + toleranceMm).IsTrue();
            await Assert.That(
                topMm <= FeetToMm(outline.Max.V) + toleranceMm).IsTrue();
        }
    }

    private Outline GetOnlyViewportBox(ViewSheet sheet)
    {
        foreach (var viewportId in sheet.GetAllViewports())
        {
            var viewport = (Viewport)Doc.GetElement(viewportId)!;
            return viewport.GetBoxOutline();
        }

        throw new InvalidOperationException("Лист " + sheet.SheetNumber + " без видовых окон");
    }

    private ScheduleSheetInstance? FindScheduleOnSheet(ViewSheet sheet)
    {
        foreach (var element in new FilteredElementCollector(Doc)
                     .OfClass(typeof(ScheduleSheetInstance)))
        {
            if (element is ScheduleSheetInstance instance && instance.OwnerViewId == sheet.Id)
                return instance;
        }

        return null;
    }

    private static SchedulableField? FindSchedulableMark(ScheduleDefinition definition)
    {
        foreach (var candidate in definition.GetSchedulableFields())
        {
            if (SafeParameterId(candidate) == (long)BuiltInParameter.ALL_MODEL_MARK)
                return candidate;
        }

        return null;
    }

    private static long SafeParameterId(SchedulableField candidate)
    {
        try
        {
            var id = candidate.ParameterId;
            return id is null || id == ElementId.InvalidElementId ? 0 : id.GetValue();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static double FeetToMm(double internalFeet) =>
        UnitUtils.ConvertFromInternalUnits(internalFeet, UnitTypeId.Millimeters);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

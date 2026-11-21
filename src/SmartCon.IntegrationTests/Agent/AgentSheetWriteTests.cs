using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

public sealed partial class AgentSheetWriteTests : RevitApiTest
{
    private const string LevelName = "SC_SheetWrite_Level";
    private const string SheetNumber = "SC-W-100";
    private const string SheetName = "SC_SheetWrite_Sheet";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private bool _sheetCreated;
    private long _sheetId;
    private long _levelId;
    private long _planId;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed sheet write model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 0.0);
            level.Name = LevelName;
            _levelId = level.Id.GetValue();

            var plan = ModelSeed.CreateViewPlanChecked(doc, level.Id, "SC_SheetWrite_Plan");
            _planId = plan.Id.GetValue();

            // Viewport.Create returns null on a document that was never
            // regenerated with model geometry (IT-verified on Revit 2025);
            // one wall is enough to warm the document up.
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

            var sheet = ViewSheet.Create(doc, titleBlockId);
            sheet.SheetNumber = SheetNumber;
            sheet.Name = SheetName;
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
    public async Task CreateSheet_DryRun_NoChanges()
    {
        var tool = CreateTool<CreateSheetTool>();
        var result = tool.ExecuteForTests(
            Parse("""{"number":"SC-W-200","name":"SC Dry Run Sheet","dryRun":true}"""),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(FindSheet("SC-W-200")).IsNull();
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CreateSheet_Applied_CreatesWithNumber()
    {
        var tool = CreateTool<CreateSheetTool>();
        var result = tool.ExecuteForTests(
            Parse("""{"number":"SC-W-201","name":"SC Applied Sheet","dryRun":false}"""),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var sheet = FindSheet("SC-W-201");
        await Assert.That(sheet).IsNotNull();
        await Assert.That(sheet!.Name).IsEqualTo("SC Applied Sheet");

        var set = _changeSets!.Last!;
        await Assert.That(set.Header.Operation).IsEqualTo("create_sheet");
        await Assert.That(set.Items[0].ParameterName).IsEqualTo("sheet_created");
        await Assert.That(set.Items[0].NewValue).IsEqualTo("SC-W-201");
        await Assert.That(result.Data!.Value.GetProperty("sheet").GetProperty("sheetId").GetInt64())
            .IsEqualTo(sheet.Id.GetValue());
    }

    [Test]
    public async Task CreateSheet_DuplicateNumber_TypedConflict()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var tool = CreateTool<CreateSheetTool>();
        var result = tool.ExecuteForTests(
            Parse("""{"number":"SC-W-100","dryRun":false}"""),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("sheet_number_taken");
        await Assert.That(result.Data!.Value.GetProperty("owner").GetProperty("sheetId").GetInt64())
            .IsEqualTo(_sheetId);
    }

    [Test]
    public async Task CreateViewports_Batch20_OneChangeSet_CoordinatesRoundTrip()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var viewIds = new List<long>();
        _transactions!.RunInTransaction(Doc, "Seed 20 plans", doc =>
        {
            for (var index = 1; index <= 20; index++)
            {
                var plan = ModelSeed.CreateViewPlanChecked(
                    doc,
                    ElementIdCompat.Create(_levelId),
                    "SC_SheetW_Plan_" + index.ToString("D2"));
                viewIds.Add(plan.Id.GetValue());
            }
        });

        var items = new List<string>();
        for (var index = 0; index < viewIds.Count; index++)
        {
            var column = index % 5;
            var row = index / 5;
            var x = 150 + column * 180;
            var y = 100 + row * 180;
            items.Add(
                """{"viewId":""" + viewIds[index] + ""","pointMmX":""" + x + ""","pointMmY":""" + y + "}");
        }

        var tool = CreateTool<CreateViewportsTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"sheetId\":" + _sheetId + ",\"items\":[" + string.Join(",", items) +
                  "],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.AffectedCount).IsEqualTo(20);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(1);

        await Assert.That(GetSheet().GetAllViewports().Count).IsEqualTo(20);

        var rows = result.Data!.Value.GetProperty("items").EnumerateArray().ToList();
        await Assert.That(rows.Count).IsEqualTo(20);
        foreach (var row in rows)
        {
            await Assert.That(row.GetProperty("status").GetString()).IsEqualTo("placed");
            var requestedX = row.GetProperty("requestedMmX").GetDouble();
            var requestedY = row.GetProperty("requestedMmY").GetDouble();
            await Assert.That(
                Math.Abs(row.GetProperty("actualMmX").GetDouble() - requestedX) < 0.5).IsTrue();
            await Assert.That(
                Math.Abs(row.GetProperty("actualMmY").GetDouble() - requestedY) < 0.5).IsTrue();
        }

        var set = _changeSets.Last!;
        await Assert.That(set.Header.Operation).IsEqualTo("create_viewports");
        await Assert.That(set.Items.Count).IsEqualTo(20);
        await Assert.That(set.Items[0].ParameterName).IsEqualTo("viewport_created");
    }

    [Test]
    public async Task CreateViewports_SecondCall_NoChanges()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var tool = CreateTool<CreateViewportsTool>();
        var args = Parse(
            "{\"sheetId\":" + _sheetId + ",\"items\":[{\"viewId\":" + _planId +
            ",\"pointMmX\":200,\"pointMmY\":200}],\"dryRun\":false}");
        var first = tool.ExecuteForTests(args, approved: true, CancellationToken.None);
        await Assert.That(first.Success).IsTrue();

        var second = tool.ExecuteForTests(args, approved: true, CancellationToken.None);
        await Assert.That(second.Success).IsTrue();
        await Assert.That(second.Data!.Value.GetProperty("noChanges").GetBoolean()).IsTrue();
        await Assert.That(GetSheet().GetAllViewports().Count).IsEqualTo(1);
    }

    [Test]
    public async Task CreateViewports_TemplateView_Rejected()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        long templateId = 0;
        _transactions!.RunInTransaction(Doc, "Seed view template", doc =>
        {
            var plan = (ViewPlan)doc.GetElement(ElementIdCompat.Create(_planId))!;
            templateId = plan.CreateViewTemplate()!.Id.GetValue();
        });

        var tool = CreateTool<CreateViewportsTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"sheetId\":" + _sheetId + ",\"items\":[{\"viewId\":" + templateId +
                  ",\"pointMmX\":100,\"pointMmY\":100}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("view_not_placeable");
        var failed = result.Data!.Value.GetProperty("failed");
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("view_not_placeable");
    }

    [Test]
    public async Task MoveViewport_ReadBackCoordinates()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        long viewportId = 0;
        _transactions!.RunInTransaction(Doc, "Seed viewport", doc =>
        {
            doc.Regenerate();
            var viewport = Viewport.Create(
                doc,
                ElementIdCompat.Create(_sheetId),
                ElementIdCompat.Create(_planId),
                new XYZ(0.3, 0.3, 0));
            viewportId = viewport.Id.GetValue();
        });

        var tool = CreateTool<MoveViewportTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"viewportId\":" + viewportId + ",\"pointMmX\":350,\"pointMmY\":420," +
                  "\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(Math.Abs(result.Data!.Value.GetProperty("actualMmX").GetDouble() - 350) < 0.5)
            .IsTrue();
        await Assert.That(Math.Abs(result.Data.Value.GetProperty("actualMmY").GetDouble() - 420) < 0.5)
            .IsTrue();

        var viewport = (Viewport)Doc.GetElement(ElementIdCompat.Create(viewportId))!;
        var center = viewport.GetBoxCenter();
        await Assert.That(Math.Abs(
            UnitUtils.ConvertFromInternalUnits(center.X, UnitTypeId.Millimeters) - 350) < 0.5).IsTrue();
        await Assert.That(Math.Abs(
            UnitUtils.ConvertFromInternalUnits(center.Y, UnitTypeId.Millimeters) - 420) < 0.5).IsTrue();

        var set = _changeSets!.Last!;
        await Assert.That(set.Items[0].ParameterName).IsEqualTo("viewport_center");
    }

    [Test]
    public async Task SetSheetNumber_Taken_TypedConflict()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        long otherSheetId = 0;
        _transactions!.RunInTransaction(Doc, "Seed second sheet", doc =>
        {
            var titleBlockId = ModelSeed.FindTitleBlockTypeId(doc);
            var other = ViewSheet.Create(doc, titleBlockId!);
            other.SheetNumber = "SC-W-102";
            otherSheetId = other.Id.GetValue();
        });

        var tool = CreateTool<SetSheetNumberTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"sheetId\":" + otherSheetId + ",\"newNumber\":\"SC-W-100\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("sheet_number_taken");
        await Assert.That(result.Data!.Value.GetProperty("owner").GetProperty("sheetId").GetInt64())
            .IsEqualTo(_sheetId);
    }

    [Test]
    public async Task SetSheetNumber_ForbiddenChars_TypedError()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var tool = CreateTool<SetSheetNumberTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"sheetId\":" + _sheetId + ",\"newNumber\":\"SC{1}\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("sheet_number_invalid");
        await Assert.That(((ViewSheet)Doc.GetElement(ElementIdCompat.Create(_sheetId))!).SheetNumber)
            .IsEqualTo(SheetNumber);
    }

    [Test]
    public async Task Sanitize_InvisibleNumber_FixedAndCollisionTyped()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var titleBlockId = ModelSeed.FindTitleBlockTypeId(Doc);
        if (titleBlockId is null)
            Skip.Test("Основная надпись недоступна");

        try
        {
            _transactions!.RunInTransaction(Doc, "Seed invisible numbers", doc =>
            {
                var cleanable = ViewSheet.Create(doc, titleBlockId);
                cleanable.SheetNumber = "\u202ASC-W-7\u202C";

                var occupied = ViewSheet.Create(doc, titleBlockId);
                occupied.SheetNumber = "SC-W-8";

                var colliding = ViewSheet.Create(doc, titleBlockId);
                colliding.SheetNumber = "\u202ASC-W-8\u202C";
            });
        }
        catch (Exception)
        {
            Skip.Test("Revit не разрешает номер листа с невидимым символом");
        }

        var tool = CreateTool<SanitizeSheetNumbersTool>();
        var result = tool.ExecuteForTests(
            Parse("""{"dryRun":false}"""),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(FindSheet("\u202ASC-W-7\u202C")).IsNull();
        await Assert.That(FindSheet("SC-W-7")).IsNotNull();

        var failed = result.Data!.Value.GetProperty("failed").EnumerateArray().ToList();
        await Assert.That(failed.Count).IsEqualTo(1);
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("sheet_number_taken");
        await Assert.That(FindSheet("\u202ASC-W-8\u202C")).IsNotNull();
    }

    [Test]
    public async Task UndoLast_AfterCreateViewports_DeletesViewports()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var createTool = CreateTool<CreateViewportsTool>();
        var created = createTool.ExecuteForTests(
            Parse("{\"sheetId\":" + _sheetId + ",\"items\":[{\"viewId\":" + _planId +
                  ",\"pointMmX\":200,\"pointMmY\":200}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(created.Success).IsTrue();

        await Assert.That(GetSheet().GetAllViewports().Count).IsEqualTo(1);

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undo = undoTool.ExecuteForTests(
            Parse("""{"dryRun":false}"""),
            approved: true,
            CancellationToken.None);

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(GetSheet().GetAllViewports().Count).IsEqualTo(0);
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(_planId))).IsNotNull();
        await Assert.That(_changeSets!.Last!.Header.AppliedAt).IsNull();
    }

    [Test]
    public async Task UndoLast_AfterSetSheetNumber_RestoresOld()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var setTool = CreateTool<SetSheetNumberTool>();
        var set = setTool.ExecuteForTests(
            Parse("{\"sheetId\":" + _sheetId + ",\"newNumber\":\"SC-W-900\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(set.Success).IsTrue();
        await Assert.That(((ViewSheet)Doc.GetElement(ElementIdCompat.Create(_sheetId))!).SheetNumber)
            .IsEqualTo("SC-W-900");

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undo = undoTool.ExecuteForTests(
            Parse("""{"dryRun":false}"""),
            approved: true,
            CancellationToken.None);

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(((ViewSheet)Doc.GetElement(ElementIdCompat.Create(_sheetId))!).SheetNumber)
            .IsEqualTo(SheetNumber);
    }

    private T CreateTool<T>()
        where T : AgentWriteToolBase
    {
        var context = new StubRevitContext(Doc);
        return typeof(T).Name switch
        {
            nameof(CreateSheetTool) => (T)(object)new CreateSheetTool(
                context, _transactions!, _changeSets!),
            nameof(CreateViewportsTool) => (T)(object)new CreateViewportsTool(
                context, _transactions!, _changeSets!),
            nameof(MoveViewportTool) => (T)(object)new MoveViewportTool(
                context, _transactions!, _changeSets!),
            nameof(SetSheetNumberTool) => (T)(object)new SetSheetNumberTool(
                context, _transactions!, _changeSets!),
            nameof(RenameSheetTool) => (T)(object)new RenameSheetTool(
                context, _transactions!, _changeSets!),
            nameof(SanitizeSheetNumbersTool) => (T)(object)new SanitizeSheetNumbersTool(
                context, _transactions!, _changeSets!),
            nameof(MoveViewportsTool) => (T)(object)new MoveViewportsTool(
                context, _transactions!, _changeSets!),
            nameof(RemoveViewportsTool) => (T)(object)new RemoveViewportsTool(
                context, _transactions!, _changeSets!),
            nameof(RenumberSheetsTool) => (T)(object)new RenumberSheetsTool(
                context, _transactions!, _changeSets!),
            _ => throw new ArgumentOutOfRangeException(nameof(T), typeof(T).Name)
        };
    }

    private ViewSheet GetSheet() => (ViewSheet)Doc.GetElement(ElementIdCompat.Create(_sheetId))!;

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

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

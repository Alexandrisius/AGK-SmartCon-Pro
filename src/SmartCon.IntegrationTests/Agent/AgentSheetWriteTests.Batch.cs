using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// G.5a/G.5b/G.5c batch sheet tools: bulk viewport moves with a shared shift,
/// unplacing views (the views stay in the project) and sequential renumbering
/// with temporary numbers, including undo of every operation.
/// </summary>
public sealed partial class AgentSheetWriteTests
{
    private List<long> SeedViewports(int count, string namePrefix)
    {
        var viewportIds = new List<long>();
        _transactions!.RunInTransaction(Doc, "Seed " + count + " viewports", doc =>
        {
            for (var index = 1; index <= count; index++)
            {
                var plan = ModelSeed.CreateViewPlanChecked(
                    doc,
                    ElementIdCompat.Create(_levelId),
                    namePrefix + index.ToString("D2"));
                var viewport = CreateViewportChecked(doc, _sheetId, plan.Id, new XYZ(0.2, 0.2, 0));
                viewportIds.Add(viewport.Id.GetValue());
            }
        });

        return viewportIds;
    }

    /// <summary>
    /// Viewport.Create returns null on a document that was not regenerated
    /// after its last change (for example a view created in the same
    /// transaction) — one regeneration makes the same call succeed.
    /// </summary>
    private static Viewport CreateViewportChecked(
        Document doc,
        long sheetId,
        ElementId viewId,
        XYZ point)
    {
        doc.Regenerate();
        var viewport = Viewport.Create(doc, ElementIdCompat.Create(sheetId), viewId, point);
        if (viewport is null)
        {
            doc.Regenerate();
            viewport = Viewport.Create(doc, ElementIdCompat.Create(sheetId), viewId, point);
        }

        return viewport!;
    }

    private static (double MmX, double MmY) BoxCenterMm(Viewport viewport)
    {
        var center = viewport.GetBoxCenter();
        return (UnitUtils.ConvertFromInternalUnits(center.X, UnitTypeId.Millimeters),
            UnitUtils.ConvertFromInternalUnits(center.Y, UnitTypeId.Millimeters));
    }

    private (double MmX, double MmY) BoxCenterMm(long viewportId) =>
        BoxCenterMm((Viewport)Doc.GetElement(ElementIdCompat.Create(viewportId))!);

    private Viewport? FindViewportOfView(long viewId)
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(Viewport)))
        {
            if (element is Viewport viewport && viewport.ViewId.GetValue() == viewId)
                return viewport;
        }

        return null;
    }

    private static ViewSheet CreateSheetChecked(Document doc, string number, string name)
    {
        var titleBlockId = ModelSeed.FindTitleBlockTypeId(doc)!;
        var sheet = ViewSheet.Create(doc, titleBlockId);
        sheet.SheetNumber = number;
        sheet.Name = name;
        return sheet;
    }

    [Test]
    public async Task MoveViewports_Batch20_OneChangeSet_UndoRestores()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var seededIds = SeedViewports(20, "SC_SheetB_Move_");
        var before = seededIds.Select(BoxCenterMm).ToList();

        var items = new List<string>();
        for (var index = 0; index < seededIds.Count; index++)
        {
            var column = index % 5;
            var row = index / 5;
            items.Add("{\"viewportId\":" + seededIds[index] +
                      ",\"pointMmX\":" + (150 + column * 180) +
                      ",\"pointMmY\":" + (100 + row * 180) + "}");
        }

        var tool = CreateTool<MoveViewportsTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"items\":[" + string.Join(",", items) + "],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.AffectedCount).IsEqualTo(20);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(1);

        var set = _changeSets.Last!;
        await Assert.That(set.Header.Operation).IsEqualTo("move_viewports");
        await Assert.That(set.Items.Count).IsEqualTo(20);
        await Assert.That(set.Items[0].ParameterName).IsEqualTo("viewport_center");

        for (var index = 0; index < seededIds.Count; index++)
        {
            var (mmX, _) = BoxCenterMm(seededIds[index]);
            await Assert.That(Math.Abs(mmX - before[index].MmX) > 1).IsTrue();
        }

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undo = undoTool.ExecuteForTests(
            Parse("""{"dryRun":false}"""),
            approved: true,
            CancellationToken.None);

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(undo.AffectedCount).IsEqualTo(20);
        for (var index = 0; index < seededIds.Count; index++)
        {
            var (mmX, mmY) = BoxCenterMm(seededIds[index]);
            await Assert.That(Math.Abs(mmX - before[index].MmX) < 0.5).IsTrue();
            await Assert.That(Math.Abs(mmY - before[index].MmY) < 0.5).IsTrue();
        }
    }

    [Test]
    public async Task MoveViewports_ShiftMode_MovesByDelta()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var seededIds = SeedViewports(3, "SC_SheetB_Shift_");
        var before = seededIds.Select(BoxCenterMm).ToList();

        var items = string.Join(",",
            seededIds.Select(id => "{\"viewportId\":" + id + "}"));

        var tool = CreateTool<MoveViewportsTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"items\":[" + items + "],\"shiftMm\":{\"dx\":100,\"dy\":-50}," +
                  "\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.AffectedCount).IsEqualTo(3);

        for (var index = 0; index < seededIds.Count; index++)
        {
            var (mmX, mmY) = BoxCenterMm(seededIds[index]);
            await Assert.That(Math.Abs(mmX - (before[index].MmX + 100)) < 0.5).IsTrue();
            await Assert.That(Math.Abs(mmY - (before[index].MmY - 50)) < 0.5).IsTrue();
        }
    }

    [Test]
    public async Task MoveViewports_MissingViewport_PerItemTypedError()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var seededIds = SeedViewports(1, "SC_SheetB_Missing_");
        var tool = CreateTool<MoveViewportsTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"items\":[{\"viewportId\":" + seededIds[0] +
                  ",\"pointMmX\":300,\"pointMmY\":300},{\"viewportId\":999999," +
                  "\"pointMmX\":100,\"pointMmY\":100}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("partialFailure").GetBoolean()).IsTrue();
        var failed = result.Data.Value.GetProperty("failed").EnumerateArray().ToList();
        await Assert.That(failed.Count).IsEqualTo(1);
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("viewport_not_found");
    }

    [Test]
    public async Task RemoveViewports_ViewKept_UndoRestoresPlacement()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        long viewId = 0;
        long viewportId = 0;
        _transactions!.RunInTransaction(Doc, "Seed one viewport", doc =>
        {
            var viewport = CreateViewportChecked(
                doc, _sheetId, ElementIdCompat.Create(_planId), new XYZ(0.3, 0.3, 0));
            viewportId = viewport.Id.GetValue();
            viewId = viewport.ViewId.GetValue();
        });

        var before = BoxCenterMm((Viewport)Doc.GetElement(ElementIdCompat.Create(viewportId))!);

        var tool = CreateTool<RemoveViewportsTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"viewportIds\":[" + viewportId + "],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(GetSheet().GetAllViewports().Count).IsEqualTo(0);
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(viewId))).IsNotNull();

        var kept = result.Data!.Value.GetProperty("viewsKept").EnumerateArray().ToList();
        await Assert.That(kept.Count).IsEqualTo(1);

        var set = _changeSets!.Last!;
        await Assert.That(set.Items[0].ParameterName).IsEqualTo("viewport_removed");

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undo = undoTool.ExecuteForTests(
            Parse("""{"dryRun":false}"""),
            approved: true,
            CancellationToken.None);

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(GetSheet().GetAllViewports().Count).IsEqualTo(1);

        var restored = FindViewportOfView(viewId);
        await Assert.That(restored).IsNotNull();
        var (mmX, mmY) = BoxCenterMm(restored!);
        await Assert.That(Math.Abs(mmX - before.MmX) < 0.5).IsTrue();
        await Assert.That(Math.Abs(mmY - before.MmY) < 0.5).IsTrue();
    }

    [Test]
    public async Task RemoveViewports_BySheetAndViewIds_TypedNotFound()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        long viewId = 0;
        _transactions!.RunInTransaction(Doc, "Seed one viewport", doc =>
        {
            var plan = ModelSeed.CreateViewPlanChecked(
                doc, ElementIdCompat.Create(_levelId), "SC_SheetB_Unplace_01");
            var viewport = CreateViewportChecked(
                doc, _sheetId, plan.Id, new XYZ(0.3, 0.3, 0));
            viewId = viewport.ViewId.GetValue();
        });

        long foreignPlanId = 0;
        _transactions.RunInTransaction(Doc, "Seed unplaced plan", doc =>
        {
            var plan = ModelSeed.CreateViewPlanChecked(
                doc, ElementIdCompat.Create(_levelId), "SC_SheetB_Unplace_02");
            foreignPlanId = plan.Id.GetValue();
        });

        var tool = CreateTool<RemoveViewportsTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"sheetId\":" + _sheetId + ",\"viewIds\":[" + viewId + "," + foreignPlanId +
                  "],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(GetSheet().GetAllViewports().Count).IsEqualTo(0);

        var rows = result.Data!.Value.GetProperty("items").EnumerateArray().ToList();
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0].GetProperty("status").GetString()).IsEqualTo("removed");
        await Assert.That(rows[1].GetProperty("status").GetString()).IsEqualTo("failed");

        var failed = result.Data.Value.GetProperty("failed").EnumerateArray().ToList();
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("viewport_not_found");
    }

    [Test]
    public async Task RenumberSheets_InsertIntoMiddle_NoTempNumbers_UndoRestores()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        long firstId = 0;
        long secondId = 0;
        long thirdId = 0;
        _transactions!.RunInTransaction(Doc, "Seed renumber sheets", doc =>
        {
            firstId = CreateSheetChecked(doc, "SC-R-1", "SC Renum One").Id.GetValue();
            secondId = CreateSheetChecked(doc, "SC-R-2", "SC Renum Two").Id.GetValue();
            thirdId = CreateSheetChecked(doc, "SC-R-9", "SC Renum Nine").Id.GetValue();
        });

        var tool = CreateTool<RenumberSheetsTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"sheetIds\":[" + firstId + "," + secondId + "," + thirdId +
                  "],\"numbers\":[\"SC-R-1\",\"SC-R-3\",\"SC-R-2\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.AffectedCount).IsEqualTo(2);
        await Assert.That(((ViewSheet)Doc.GetElement(ElementIdCompat.Create(firstId))!)
            .SheetNumber).IsEqualTo("SC-R-1");
        await Assert.That(((ViewSheet)Doc.GetElement(ElementIdCompat.Create(secondId))!)
            .SheetNumber).IsEqualTo("SC-R-3");
        await Assert.That(((ViewSheet)Doc.GetElement(ElementIdCompat.Create(thirdId))!)
            .SheetNumber).IsEqualTo("SC-R-2");

        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(ViewSheet)))
        {
            var sheet = (ViewSheet)element;
            await Assert.That(
                (sheet.SheetNumber ?? string.Empty).StartsWith("__sc_tmp_")).IsFalse();
        }

        var set = _changeSets!.Last!;
        await Assert.That(set.Items.Count).IsEqualTo(2);
        await Assert.That(set.Items[0].ParameterName).IsEqualTo("sheet_number");

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undo = undoTool.ExecuteForTests(
            Parse("""{"dryRun":false}"""),
            approved: true,
            CancellationToken.None);

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(((ViewSheet)Doc.GetElement(ElementIdCompat.Create(firstId))!)
            .SheetNumber).IsEqualTo("SC-R-1");
        await Assert.That(((ViewSheet)Doc.GetElement(ElementIdCompat.Create(secondId))!)
            .SheetNumber).IsEqualTo("SC-R-2");
        await Assert.That(((ViewSheet)Doc.GetElement(ElementIdCompat.Create(thirdId))!)
            .SheetNumber).IsEqualTo("SC-R-9");
    }

    [Test]
    public async Task RenumberSheets_StartNumber_SequenceWithPadding()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var ids = new List<long>();
        _transactions!.RunInTransaction(Doc, "Seed sequence sheets", doc =>
        {
            for (var index = 1; index <= 3; index++)
                ids.Add(CreateSheetChecked(
                    doc, "SC-S-TMP" + index, "SC Seq " + index).Id.GetValue());
        });

        var tool = CreateTool<RenumberSheetsTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"sheetIds\":[" + string.Join(",", ids) +
                  "],\"startNumber\":\"SC-S-05\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.AffectedCount).IsEqualTo(3);

        var expected = new[] { "SC-S-05", "SC-S-06", "SC-S-07" };
        for (var index = 0; index < ids.Count; index++)
            await Assert.That(((ViewSheet)Doc.GetElement(ElementIdCompat.Create(ids[index]))!)
                .SheetNumber).IsEqualTo(expected[index]);
    }

    [Test]
    public async Task RenumberSheets_TakenByForeignSheet_TypedPerItem()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        long firstId = 0;
        long secondId = 0;
        long foreignId = 0;
        _transactions!.RunInTransaction(Doc, "Seed foreign owner", doc =>
        {
            firstId = CreateSheetChecked(doc, "SC-F-1", "SC Foreign One").Id.GetValue();
            secondId = CreateSheetChecked(doc, "SC-F-2", "SC Foreign Two").Id.GetValue();
            foreignId = CreateSheetChecked(doc, "SC-F-3", "SC Foreign Holder").Id.GetValue();
        });

        var tool = CreateTool<RenumberSheetsTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"sheetIds\":[" + firstId + "," + secondId +
                  "],\"numbers\":[\"SC-F-10\",\"SC-F-3\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.AffectedCount).IsEqualTo(1);
        await Assert.That(((ViewSheet)Doc.GetElement(ElementIdCompat.Create(firstId))!)
            .SheetNumber).IsEqualTo("SC-F-10");
        await Assert.That(((ViewSheet)Doc.GetElement(ElementIdCompat.Create(secondId))!)
            .SheetNumber).IsEqualTo("SC-F-2");

        var failed = result.Data!.Value.GetProperty("failed").EnumerateArray().ToList();
        await Assert.That(failed.Count).IsEqualTo(1);
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("sheet_number_taken");

        var rows = result.Data.Value.GetProperty("items").EnumerateArray().ToList();
        var failedRow = rows.First(row => row.GetProperty("status").GetString() == "failed");
        await Assert.That(failedRow.GetProperty("owner").GetProperty("sheetId").GetInt64())
            .IsEqualTo(foreignId);
    }
}

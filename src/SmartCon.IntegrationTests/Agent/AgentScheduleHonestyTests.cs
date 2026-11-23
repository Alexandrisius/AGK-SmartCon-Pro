using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Honesty round of the schedule/sheet tools (night2 S1): provisional dry-run
/// ids (C7-05/WA-5), the false grand-total warning (C4-14), fieldCount
/// semantics (C4-13), isOnSheet of placed schedules (C8-03), key schedules out
/// of audit_fill_rate (C8-04), categoryId of the overview (ORCH-8), template
/// parameter lists of a templated view (N-ds-1), discipline of views without
/// one (N-ds-2), piping elementCount (N-ds-7) and album inheritance of title
/// block values (C6-11).
/// </summary>
public sealed class AgentScheduleHonestyTests : RevitApiTest
{
    private const string ScheduleName = "SC_Honesty_Walls";
    private const string CloneName = "SC_Honesty_Clone";
    private const string NoTotalsName = "SC_Honesty_NoTotals";
    private const string NoTotalsClone = "SC_Honesty_NoTotals_Clone";
    private const string SheetNumber = "SC-HN-01";
    private const string PlanName = "SC_Honesty_Plan";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private long _scheduleId;
    private long _planId;
    private bool _sheetCreated;
    private string _markField = "Mark";
    private string _commentsField = "Comments";

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed honesty model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 0.0);

            for (var index = 0; index < 2; index++)
            {
                var wall = ModelSeed.TryCreateWallAt(doc, level.Id, index * 3.0);
                wall?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                    ?.Set("SC_Honesty_Wall");
            }

            var schedule = ModelSeed.CreateScheduleChecked(doc);
            schedule.Name = ScheduleName;
            _scheduleId = schedule.Id.GetValue();
            var definition = schedule.Definition;

            var markSchedulable = FindSchedulable(
                doc, definition, BuiltInParameter.ALL_MODEL_MARK);
            var commentsSchedulable = FindSchedulable(
                doc, definition, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (markSchedulable is null || commentsSchedulable is null)
            {
                Skip.Test("В шаблоне нет назначаемых полей Mark/Comments для стен");
                return;
            }

            var markField = definition.AddField(markSchedulable);
            var commentsField = definition.AddField(commentsSchedulable);
            _markField = markField.GetName();
            _commentsField = commentsField.GetName();

            var plan = ModelSeed.CreateViewPlanChecked(doc, level.Id, PlanName);
            _planId = plan.Id.GetValue();

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
            sheet.Name = "SC_Honesty_Sheet";
            _sheetCreated = true;
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    private static SchedulableField? FindSchedulable(
        Document doc,
        ScheduleDefinition definition,
        BuiltInParameter parameter)
    {
        foreach (var field in definition.GetSchedulableFields())
        {
            if (field.ParameterId == new ElementId(parameter))
                return field;
        }

        return null;
    }

    [Test]
    public async Task CreateSchedule_DryRun_CreatedBlockNamesProvisionalId()
    {
        var tool = CreateTool<CreateScheduleTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"category\":\"OST_Walls\",\"name\":\"" + ScheduleName +
                  "_dry\",\"fields\":[\"" + _markField + "\"],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();

        // C7-05: the id of the rolled-back schedule lives in created{}, and the
        // warning names it PROVISIONAL — the model must not reference it.
        var created = result.Data!.Value.GetProperty("created");
        await Assert.That(created.GetProperty("name").GetString())
            .IsEqualTo(ScheduleName + "_dry");
        var dryId = created.GetProperty("scheduleId").GetInt64();
        await Assert.That(dryId).IsGreaterThan(0);

        await Assert.That(result.Warnings.Any(warning =>
            warning.Contains("PROVISIONAL", StringComparison.OrdinalIgnoreCase))).IsTrue();

        await Assert.That(FindSchedule(ScheduleName + "_dry")).IsNull();
        await Assert.That(result.Data.Value.GetProperty("schedule").TryGetProperty(
            "scheduleId", out _)).IsFalse();
    }

    [Test]
    public async Task CloneSchedule_DryRun_CreatedBlockNamesProvisionalId()
    {
        var tool = CreateTool<CloneScheduleTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"sourceScheduleId\":" + _scheduleId + ",\"name\":\"" + CloneName +
                  "\",\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var created = result.Data!.Value.GetProperty("created");
        await Assert.That(created.GetProperty("name").GetString()).IsEqualTo(CloneName);
        await Assert.That(created.GetProperty("scheduleId").GetInt64()).IsGreaterThan(0);

        var warnings = result.Data.Value.GetProperty("warnings").EnumerateArray()
            .Select(value => value.GetString())
            .ToList();
        await Assert.That(warnings.Any(warning =>
            warning?.Contains("PROVISIONAL", StringComparison.OrdinalIgnoreCase) == true)).IsTrue();

        await Assert.That(FindSchedule(CloneName)).IsNull();
    }

    [Test]
    public async Task CloneSchedule_SourceWithoutGrandTotal_NoGrandTotalWarning()
    {
        _transactions!.RunInTransaction(Doc, "Seed source without totals", doc =>
        {
            var source = ModelSeed.CreateScheduleChecked(doc);
            source.Name = NoTotalsName;
            source.Definition.AddField(FindSchedulable(
                doc, source.Definition, BuiltInParameter.ALL_MODEL_MARK)!);
        });
        var noTotalsId = FindSchedule(NoTotalsName)!.Id.GetValue();

        var tool = CreateTool<CloneScheduleTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"sourceScheduleId\":" + noTotalsId + ",\"name\":\"" + NoTotalsClone +
                  "\",\"copyTotals\":true,\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();

        // C4-14: the source shows no grand total row — the clone has nothing to
        // transfer, so no "grand total row was not transferred" warning.
        var warnings = result.Data!.Value.GetProperty("warnings").EnumerateArray()
            .Select(value => value.GetString())
            .ToList();
        await Assert.That(warnings.Any(warning =>
            warning?.Contains("grand total", StringComparison.OrdinalIgnoreCase) == true)).IsFalse();
    }

    [Test]
    public async Task ListSchedules_FieldCount_CountsVisibleAndHidden()
    {
        var hide = CreateTool<SetScheduleFieldVisibilityTool>();
        var hidden = hide.ExecuteForTests(
            Parse("{\"scheduleId\":" + _scheduleId + ",\"fields\":[{\"name\":\"" +
                  _commentsField + "\",\"hidden\":true}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(hidden.Success).IsTrue();

        var tool = new ListSchedulesTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"nameContains\":\"" + ScheduleName + "\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var card = result.Data!.Value.GetProperty("schedules")[0];

        // C4-13: fieldCount counts ALL fields (visible + hidden) — the same
        // meaning audit_fill_rate reports; fields[] lists only visible names.
        await Assert.That(card.GetProperty("fieldCount").GetInt32()).IsEqualTo(2);
        await Assert.That(card.GetProperty("hiddenFieldCount").GetInt32()).IsEqualTo(1);
        var fields = card.GetProperty("fields").EnumerateArray()
            .Select(value => value.GetString())
            .ToList();
        await Assert.That(fields.Count).IsEqualTo(1);
        await Assert.That(fields[0]).IsEqualTo(_markField);
    }

    [Test]
    public async Task ListViews_PlacedSchedule_IsOnSheetWithSheetNumber()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне нет титульного блока для листа");

        var place = CreateTool<PlaceScheduleOnSheetTool>();
        var placed = place.ExecuteForTests(
            Parse("{\"scheduleId\":" + _scheduleId + ",\"sheetNumber\":\"" + SheetNumber +
                  "\",\"pointMmX\":100,\"pointMmY\":100,\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(placed.Success).IsTrue();

        var tool = new ListViewsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"types\":[\"Schedule\"],\"nameContains\":\"" + ScheduleName + "\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var views = result.Data!.Value.GetProperty("views");
        await Assert.That(views.GetArrayLength()).IsEqualTo(1);

        // C8-03: a schedule placed via ScheduleSheetInstance carries no
        // VIEWER_SHEET_NUMBER — isOnSheet must still be true with the number.
        await Assert.That(views[0].GetProperty("isOnSheet").GetBoolean()).IsTrue();
        await Assert.That(views[0].GetProperty("sheetNumber").GetString())
            .IsEqualTo(SheetNumber);
    }

    [Test]
    public async Task AuditFillRate_KeySchedule_ExcludedFromSources()
    {
        long keyId = 0;
        _transactions!.RunInTransaction(Doc, "Seed key schedule", doc =>
        {
            try
            {
                var key = ViewSchedule.CreateKeySchedule(
                    doc, new ElementId(BuiltInCategory.OST_Walls));
                key.Name = "SC_Honesty_Key";
                keyId = key.Id.GetValue();
            }
            catch (Exception)
            {
                // Key schedule creation may be refused by the template.
            }
        });
        if (keyId == 0)
            Skip.Test("Revit не создал ключевую спецификацию в этом шаблоне");

        var tool = new AuditFillRateTool(new StubRevitContext(Doc));

        var onlyKey = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleIds\":[" + keyId + "]}"),
            CancellationToken.None);

        // C8-04: a key schedule alone is no source — the honest refusal.
        await Assert.That(onlyKey.Success).IsFalse();
        await Assert.That(onlyKey.ErrorCode).IsEqualTo("no_source_schedules");

        var withWallSchedule = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleIds\":[" + keyId + "," + _scheduleId + "]}"),
            CancellationToken.None);

        await Assert.That(withWallSchedule.Success).IsTrue();
        var skipped = withWallSchedule.Data!.Value.GetProperty("keySchedulesSkipped");
        await Assert.That(skipped.GetArrayLength()).IsEqualTo(1);
        await Assert.That(skipped[0].GetProperty("scheduleId").GetInt64()).IsEqualTo(keyId);

        var sources = withWallSchedule.Data.Value.GetProperty("sourceSchedules")
            .EnumerateArray()
            .Select(row => row.GetProperty("scheduleId").GetInt64())
            .ToList();
        await Assert.That(sources).DoesNotContain(keyId);
        await Assert.That(sources).Contains(_scheduleId);
    }

    [Test]
    public async Task GetProjectOverview_CategoriesCarryRealIds()
    {
        var tool = new GetProjectOverviewTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var categories = result.Data!.Value.GetProperty("categories").EnumerateArray().ToList();
        await Assert.That(categories.Count).IsGreaterThan(0);

        // ORCH-8: categoryId was never set — every row used to report 0.
        var walls = categories.Single(row =>
            row.GetProperty("displayName").GetString() == "Walls");
        await Assert.That(walls.GetProperty("categoryId").GetInt64()).IsNotEqualTo(0);
    }

    [Test]
    public async Task GetViewSettings_TemplatedView_ListsTemplateControlledParameters()
    {
        View? plan = null;
        _transactions!.RunInTransaction(Doc, "Seed template", doc =>
        {
            plan = (View)doc.GetElement(ElementIdCompat.Create(_planId))!;
            var template = plan.CreateViewTemplate();
            template.Name = "SC_Honesty_Template";
            plan.ViewTemplateId = template.Id;
        });
        await Assert.That(plan!.ViewTemplateId).IsNotEqualTo(ElementId.InvalidElementId);

        var tool = new GetViewSettingsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"viewId\":" + _planId +
                  ",\"fields\":[\"controlledParameters\",\"nonControlledParameters\"]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();

        // N-ds-1: a templated view reports its TEMPLATE's split — the view
        // itself has no template ids, so both arrays used to be empty.
        var controlled = result.Data!.Value.GetProperty("controlledParameters");
        await Assert.That(controlled.GetArrayLength()).IsGreaterThan(0);
        await Assert.That(result.Data.Value.GetProperty("nonControlledParameters"))
            .IsNotNull();
    }

    [Test]
    public async Task GetViewSettings_BatchDiscipline_ViewWithoutDisciplineDoesNotFail()
    {
        var tool = new GetViewSettingsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"viewIds\":[" + _planId + "," + _scheduleId +
                  "],\"fields\":[\"discipline\"]}"),
            CancellationToken.None);

        // N-ds-2: one view without a Discipline property must not kill the
        // batch; its discipline is honestly null.
        await Assert.That(result.Success).IsTrue();
        var views = result.Data!.Value.GetProperty("views");
        await Assert.That(views.GetArrayLength()).IsEqualTo(2);

        var scheduleDisciplineThrows = false;
        try
        {
            _ = ((View)Doc.GetElement(ElementIdCompat.Create(_scheduleId))!).Discipline;
        }
        catch (Exception)
        {
            scheduleDisciplineThrows = true;
        }

        var scheduleRow = views.EnumerateArray()
            .Single(row => row.GetProperty("viewId").GetInt64() == _scheduleId);
        if (scheduleDisciplineThrows)
            await Assert.That(scheduleRow.GetProperty("discipline").ValueKind)
                .IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task ListMepSystems_PipesAssignedToSystem_CountedInElementCount()
    {
        const string systemName = "SC_Honesty_Piping";
        var reason = "неизвестная причина";
        var seeded = false;
        try
        {
            _transactions!.RunInTransaction(Doc, "Seed piping", doc =>
            {
                var systemTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(PipingSystemType))
                    .WhereElementIsElementType()
                    .FirstElementId();
                if (systemTypeId.Equals(ElementId.InvalidElementId))
                {
                    reason = "в шаблоне нет PipingSystemType";
                    return;
                }

                var system = PipingSystem.Create(doc, systemTypeId, systemName);
                var level = ModelSeed.CreateLevel(doc, 0.0);
                var assigned = 0;
                var missingParameter = false;
                for (var index = 0; index < 2; index++)
                {
                    var pipe = ModelSeed.TryCreatePipe(
                        doc, level.Id,
                        new XYZ(0, 10 + index * 3.0, 0),
                        new XYZ(10, 10 + index * 3.0, 0));
                    if (pipe is null)
                    {
                        reason = "труба не создаётся в шаблоне";
                        continue;
                    }

                    var nameParameter = pipe.get_Parameter(
                        BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
                    if (nameParameter is null || !nameParameter.Set(systemName))
                    {
                        missingParameter = true;
                        continue;
                    }

                    assigned++;
                }

                if (missingParameter && assigned == 0)
                    reason = "параметр имени системы не назначается у трубы";

                seeded = assigned == 2 && !system.Id.Equals(ElementId.InvalidElementId);
            });
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // The default template may have the MEP disciplines disabled.
            reason = "MEP-дисциплины отключены в шаблоне";
        }

        if (!seeded)
            Skip.Test(reason);

        var tool = new ListMepSystemsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"domain\":\"piping\",\"nameContains\":\"" + systemName + "\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var systems = result.Data!.Value.GetProperty("systems");
        await Assert.That(systems.GetArrayLength()).IsEqualTo(1);

        // N-ds-7: pipes of the system count in elementCount — MEPSystem.Elements
        // alone holds only terminals and reported 0.
        await Assert.That(systems[0].GetProperty("elementCount").GetInt32())
            .IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task BuildSystemSheets_NoTitleBlockParameters_InheritsAlbumValue()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне нет титульного блока для листа");

        // Revit refuses binding a project parameter to OST_TitleBlocks, so the
        // album value lives in an editable family parameter of the loaded title
        // block family (exactly the "Кратность" shape the fix targets).
        var inheritName = string.Empty;
        var inheritValue = string.Empty;
        _transactions!.RunInTransaction(Doc, "Set album value", doc =>
        {
            foreach (var element in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_TitleBlocks)
                         .WhereElementIsNotElementType())
            {
                if (element is not FamilyInstance instance)
                    continue;

                foreach (Parameter parameter in instance.Parameters)
                {
                    if (parameter.IsReadOnly
                        || parameter.StorageType
                            is not (StorageType.String or StorageType.Integer)
                        || parameter.Definition is not InternalDefinition
                            {
                                BuiltInParameter: BuiltInParameter.INVALID
                            } definition)
                        continue;

                    var set = parameter.StorageType == StorageType.String
                        ? parameter.Set("SC_HN_Album")
                        : parameter.Set(2);
                    if (!set)
                        continue;

                    inheritName = definition.Name;
                    inheritValue = parameter.StorageType == StorageType.String
                        ? "SC_HN_Album"
                        : "2";
                    break;
                }

                break;
            }
        });
        if (inheritName.Length == 0)
            Skip.Test("У титульного блока шаблона нет редактируемого строкового/целого параметра");

        var tool = CreateTool<BuildSystemSheetsTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"items\":[{\"viewId\":" + _planId +
                  ",\"sheetName\":\"SC_Honesty_New\"}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var item = result.Data!.Value.GetProperty("items")[0];
        await Assert.That(item.GetProperty("status").GetString()).IsEqualTo("built");

        // C6-11: without explicit titleBlockParameters the album's common value
        // is inherited (status "inherited"), not left empty.
        var parameters = item.GetProperty("titleBlockParameters").EnumerateArray()
            .Where(row => row.GetProperty("name").GetString() == inheritName)
            .ToList();
        await Assert.That(parameters.Count).IsEqualTo(1);
        await Assert.That(parameters[0].GetProperty("status").GetString())
            .IsEqualTo("inherited");
        await Assert.That(parameters[0].GetProperty("value").GetString())
            .IsEqualTo(inheritValue);
    }

    private ViewSchedule? FindSchedule(string name)
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(ViewSchedule)))
        {
            if (element is ViewSchedule schedule &&
                string.Equals(schedule.Name, name, StringComparison.Ordinal))
                return schedule;
        }

        return null;
    }

    private T CreateTool<T>()
        where T : AgentWriteToolBase
    {
        var context = new StubRevitContext(Doc);
        return typeof(T).Name switch
        {
            nameof(CreateScheduleTool) => (T)(object)new CreateScheduleTool(
                context, _transactions!, _changeSets!),
            nameof(CloneScheduleTool) => (T)(object)new CloneScheduleTool(
                context, _transactions!, _changeSets!),
            nameof(SetScheduleFieldVisibilityTool) => (T)(object)new SetScheduleFieldVisibilityTool(
                context, _transactions!, _changeSets!),
            nameof(PlaceScheduleOnSheetTool) => (T)(object)new PlaceScheduleOnSheetTool(
                context, _transactions!, _changeSets!),
            nameof(BuildSystemSheetsTool) => (T)(object)new BuildSystemSheetsTool(
                context, _transactions!, _changeSets!),
            _ => throw new ArgumentOutOfRangeException(nameof(T), typeof(T).Name)
        };
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

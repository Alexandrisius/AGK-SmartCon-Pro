using System.Text.Json;
using SmartCon.Core.Models.Agent;
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
/// Wave-2 rebuild tools: clone_schedule (the on-place ФОП replacement composite),
/// rename_schedule, set_schedule_field_format, remove_schedule_from_sheet and
/// the placement rotation. The seed is one ГОСТ-style wall schedule on a sheet:
/// hidden service column, custom headings/widths, filter, grouping with footer,
/// grand totals, striped rows and two shared parameter sets (old/new GUIDs).
/// </summary>
public sealed class AgentScheduleCloneTests : RevitApiTest
{
    private const string ScheduleName = "SC_Клон_Источник";
    private const string CloneName = "SC_Клон_Копия";
    private const string ArchiveName = "SC_Клон_Архив";
    private const string SheetNumber = "SC-KL-01";
    private const string WallComment = "SC_Клон_Стена";
    private const string OldCodeName = "СК_Клон_СтарыйКод";
    private const string NewCodeName = "СК_Клон_НовыйКод";
    private const string GroupName = "СК_Клон";

    private static Guid OldCodeGuid => new("11111111-2222-3333-4444-555555555555");
    private static Guid NewCodeGuid => new("66666666-7777-8888-9999-000000000000");

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private SharedParameterFileScope? _sharedScope;
    private long _sheetId;
    private long _scheduleId;
    private bool _sheetCreated;
    private string _markField = "Mark";
    private string _commentsField = "Comments";
    private string _oldCodeField = OldCodeName;
    private string _offsetField = string.Empty;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _sharedScope = SharedParameterFileScope.Create(Application);

        _transactions.RunInTransaction(Doc, "Seed clone model", doc =>
        {
            var app = doc.Application;
            var file = app.OpenSharedParameterFile();
            var group = file!.Groups.Create(GroupName);
            var oldDefinition = (ExternalDefinition)group.Definitions.Create(
                new ExternalDefinitionCreationOptions(OldCodeName, SpecTypeId.String.Text)
                {
                    GUID = OldCodeGuid
                });
            var newDefinition = (ExternalDefinition)group.Definitions.Create(
                new ExternalDefinitionCreationOptions(NewCodeName, SpecTypeId.String.Text)
                {
                    GUID = NewCodeGuid
                });

            var walls = app.Create.NewCategorySet();
            walls.Insert(Category.GetCategory(doc, BuiltInCategory.OST_Walls));
            if (!doc.ParameterBindings.Insert(
                    oldDefinition, app.Create.NewInstanceBinding(walls))
                || !doc.ParameterBindings.Insert(
                    newDefinition, app.Create.NewInstanceBinding(walls)))
            {
                Skip.Test("Не удалось привязать общие параметры к категории стен");
                return;
            }

            var level = ModelSeed.CreateLevel(doc, 0.0);
            for (var index = 0; index < 3; index++)
            {
                var wall = ModelSeed.TryCreateWallAt(doc, level.Id, index * 3.0);
                if (wall is null)
                    continue;

                wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                    ?.Set(WallComment);
                wall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)
                    ?.Set("М" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                wall.get_Parameter(OldCodeGuid)?.Set("Код-" + index);
                wall.get_Parameter(NewCodeGuid)?.Set("Новый-" + index);
            }

            var schedule = ModelSeed.CreateScheduleChecked(doc);
            schedule.Name = ScheduleName;
            _scheduleId = schedule.Id.GetValue();
            var definition = schedule.Definition;

            var markField = definition.AddField(FindSchedulableChecked(
                doc, definition, BuiltInParameter.ALL_MODEL_MARK));
            var commentsField = definition.AddField(FindSchedulableChecked(
                doc, definition, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS));
            var oldCodeField = definition.AddField(FindSchedulableByParameter(
                doc, definition, SharedParameterElement.Lookup(doc, OldCodeGuid)!.Id));
            var offsetSchedulable = FindSchedulable(
                doc, definition, BuiltInParameter.WALL_BASE_OFFSET);
            if (offsetSchedulable is null)
            {
                Skip.Test("В шаблоне нет назначаемого поля смещения основания стены");
                return;
            }

            var offsetField = definition.AddField(offsetSchedulable);

            // The column names the tools see are the FIELD names: several
            // schedulable candidates can share one parameterId (Comments vs
            // Analytical: Comments) with an unstable order, so the names are
            // taken from the added fields, never from a candidate probe.
            _markField = markField.GetName();
            _commentsField = commentsField.GetName();
            _oldCodeField = oldCodeField.GetName();
            _offsetField = offsetField.GetName();

            markField.IsHidden = true;
            commentsField.ColumnHeading = "Комментарий ГОСТ";
            oldCodeField.ColumnHeading = "Код АДСК";
            offsetField.DisplayType = ScheduleFieldDisplayType.Totals;
            AgentScheduleWriteHelper.TrySetColumnWidthMm(commentsField, 60.0, out _);
            AgentScheduleWriteHelper.TrySetColumnWidthMm(oldCodeField, 45.0, out _);

            definition.AddFilter(new ScheduleFilter(
                commentsField.FieldId, ScheduleFilterType.Equal, WallComment));
            definition.AddSortGroupField(new ScheduleSortGroupField(
                markField.FieldId, ScheduleSortOrder.Ascending)
            {
                ShowFooter = true,
                ShowBlankLine = true
            });
            try
            {
                definition.IsItemized = false;
            }
            catch (Exception)
            {
                // Key schedules refuse the flag; this one is a plain schedule.
            }

            definition.ShowGrandTotal = true;
            definition.ShowGrandTotalCount = true;

            schedule.HasStripedRows = true;
            schedule.UseStripedRowsOnSheets = true;

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
            sheet.Name = "SC_Клон_Лист";
            _sheetId = sheet.Id.GetValue();
            _sheetCreated = true;
            ScheduleSheetInstance.Create(
                doc, sheet.Id, schedule.Id,
                new XYZ(
                    AgentSheetWriteHelper.MillimetersToInternal(120),
                    AgentSheetWriteHelper.MillimetersToInternal(80),
                    0));
        });
    }

    [After(Test)]
    public void Close()
    {
        _sharedScope?.Dispose();
        Doc.Close(false);
    }

    [Test]
    public async Task CloneSchedule_FullMapping_CopiesEverything()
    {
        var clone = CreateTool<CloneScheduleTool>().ExecuteForTests(
            Parse("{\"sourceScheduleId\":" + _scheduleId + ",\"name\":\"" + CloneName +
                  "\",\"fieldMapping\":[{\"sourceField\":\"" + _oldCodeField +
                  "\",\"targetGuid\":\"" + NewCodeGuid.ToString("D") +
                  "\"}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(clone.Success).IsTrue();
        await Assert.That(clone.Data!.Value.GetProperty("skippedFields").GetArrayLength())
            .IsEqualTo(0);

        var schedule = FindSchedule(CloneName);
        await Assert.That(schedule).IsNotNull();
        var definition = schedule!.Definition;

        // Column order: hidden service field, comments, the NEW parameter, offset.
        var names = FieldNames(definition);
        await Assert.That(names.Count).IsEqualTo(4);
        await Assert.That(names[0]).IsEqualTo(_markField);
        await Assert.That(names[1]).IsEqualTo(_commentsField);
        await Assert.That(names[2]).IsEqualTo(NewCodeName);
        await Assert.That(names[3]).IsEqualTo(_offsetField);

        var cloneMark = definition.GetField(
            AgentScheduleWriteHelper.FindFieldIdByName(definition, _markField)!);
        var cloneComments = definition.GetField(
            AgentScheduleWriteHelper.FindFieldIdByName(definition, _commentsField)!);
        var cloneCode = definition.GetField(
            AgentScheduleWriteHelper.FindFieldIdByName(definition, NewCodeName)!);
        var cloneOffset = definition.GetField(
            AgentScheduleWriteHelper.FindFieldIdByName(definition, _offsetField)!);

        await Assert.That(cloneMark.IsHidden).IsTrue();
        await Assert.That(cloneComments.ColumnHeading).IsEqualTo("Комментарий ГОСТ");
        await Assert.That(cloneCode.ColumnHeading).IsEqualTo("Код АДСК");
        await Assert.That(
                Math.Abs(AgentScheduleWriteHelper.GetColumnWidthMm(cloneComments)!.Value - 60)
                    < 0.1)
            .IsTrue();
        await Assert.That(
                Math.Abs(AgentScheduleWriteHelper.GetColumnWidthMm(cloneCode)!.Value - 45) < 0.1)
            .IsTrue();

        var sorts = definition.GetSortGroupFields();
        await Assert.That(sorts.Count).IsEqualTo(1);
        await Assert.That(sorts[0].ShowFooter).IsTrue();
        await Assert.That(sorts[0].ShowBlankLine).IsTrue();
        await Assert.That(definition.IsItemized).IsFalse();

        var filters = definition.GetFilters();
        await Assert.That(filters.Count).IsEqualTo(1);
        await Assert.That(filters[0].IsStringValue && filters[0].GetStringValue() == WallComment)
            .IsTrue();

        await Assert.That(definition.ShowGrandTotal).IsTrue();
        await Assert.That(definition.ShowGrandTotalCount).IsTrue();
        await Assert.That(cloneOffset.DisplayType).IsEqualTo(ScheduleFieldDisplayType.Totals);

        await Assert.That(schedule.HasStripedRows).IsTrue();
        await Assert.That(schedule.UseStripedRowsOnSheets).IsTrue();

        // The source keeps its fields — the archive stays readable.
        var source = FindSchedule(ScheduleName);
        await Assert.That(source).IsNotNull();
        await Assert.That(
                AgentScheduleWriteHelper.FindFieldIdByName(
                    source!.Definition, _oldCodeField))
            .IsNotNull();
    }

    [Test]
    public async Task CloneSchedule_ColumnWithoutAnalog_IsSkippedAndListed()
    {
        var clone = CreateTool<CloneScheduleTool>().ExecuteForTests(
            Parse("{\"sourceScheduleId\":" + _scheduleId + ",\"name\":\"" + CloneName +
                  "\",\"fieldMapping\":[{\"sourceField\":\"" + _oldCodeField +
                  "\"}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(clone.Success).IsTrue();
        var skipped = clone.Data!.Value.GetProperty("skippedFields");
        await Assert.That(skipped.GetArrayLength()).IsEqualTo(1);
        await Assert.That(skipped[0].GetProperty("sourceField").GetString())
            .IsEqualTo(_oldCodeField);

        var definition = FindSchedule(CloneName)!.Definition;
        var names = FieldNames(definition);
        await Assert.That(names.Count).IsEqualTo(3);
        await Assert.That(names.Contains(_oldCodeField)).IsFalse();
    }

    [Test]
    public async Task CloneSchedule_TakenName_TypedConflict()
    {
        var clone = CreateTool<CloneScheduleTool>().ExecuteForTests(
            Parse("{\"sourceScheduleId\":" + _scheduleId + ",\"name\":\"" + ScheduleName +
                  "\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(clone.Success).IsFalse();
        await Assert.That(clone.ErrorCode).IsEqualTo("schedule_name_taken");
    }

    [Test]
    public async Task RenameSchedule_AppliesAndUndoRestoresOldName()
    {
        var rename = CreateTool<RenameScheduleTool>().ExecuteForTests(
            Parse("{\"scheduleId\":" + _scheduleId + ",\"newName\":\"" + ArchiveName +
                  "\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(rename.Success).IsTrue();
        await Assert.That(FindSchedule(ArchiveName)).IsNotNull();
        await Assert.That(FindSchedule(ScheduleName)).IsNull();

        var undo = UndoLast();
        await Assert.That(undo.Success).IsTrue();
        await Assert.That(FindSchedule(ScheduleName)).IsNotNull();
        await Assert.That(FindSchedule(ArchiveName)).IsNull();
    }

    [Test]
    public async Task RemoveScheduleFromSheet_RemovesAndUndoRestoresPointAndRotation()
    {
        if (!_sheetCreated)
            Skip.Test("Лист не создался в шаблоне проекта");

        var rotate = CreateTool<MoveScheduleOnSheetTool>().ExecuteForTests(
            Parse("{\"scheduleId\":" + _scheduleId + ",\"sheetNumber\":\"" + SheetNumber +
                  "\",\"pointMmX\":120,\"pointMmY\":80,\"rotation\":\"cw90\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(rotate.Success).IsTrue();
        await Assert.That(rotate.Data!.Value.GetProperty("rotation").GetString())
            .IsEqualTo("cw90");

        var remove = CreateTool<RemoveScheduleFromSheetTool>().ExecuteForTests(
            Parse("{\"scheduleId\":" + _scheduleId + ",\"sheetNumber\":\"" + SheetNumber +
                  "\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(remove.Success).IsTrue();
        await Assert.That(PlacementsOf(_scheduleId).Count).IsEqualTo(0);

        var undo = UndoLast();
        await Assert.That(undo.Success).IsTrue();

        var placements = PlacementsOf(_scheduleId);
        await Assert.That(placements.Count).IsEqualTo(1);
        var restored = placements[0];
        await Assert.That(
                Math.Abs(AgentUnitsFormat.ToMillimeters(restored.Point.X) - 120) < 0.5)
            .IsTrue();
        await Assert.That(
                Math.Abs(AgentUnitsFormat.ToMillimeters(restored.Point.Y) - 80) < 0.5)
            .IsTrue();
        await Assert.That(restored.Rotation).IsEqualTo(ViewportRotation.Clockwise);
    }

    [Test]
    public async Task PlaceScheduleOnSheet_Rotation_ReadBackInDefinition()
    {
        if (!_sheetCreated)
            Skip.Test("Лист не создался в шаблоне проекта");

        // A fresh schedule: the seeded one is already placed on this sheet and
        // place_schedule_on_sheet skips duplicates by design.
        var clone = CreateTool<CloneScheduleTool>().ExecuteForTests(
            Parse("{\"sourceScheduleId\":" + _scheduleId + ",\"name\":\"" + CloneName +
                  "\",\"copyFilters\":false,\"copySort\":false,\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(clone.Success).IsTrue();
        var cloneId = clone.Data!.Value.GetProperty("created")
            .GetProperty("scheduleId").GetInt64();

        var place = CreateTool<PlaceScheduleOnSheetTool>().ExecuteForTests(
            Parse("{\"scheduleId\":" + cloneId + ",\"sheetNumber\":\"" + SheetNumber +
                  "\",\"pointMmX\":40,\"pointMmY\":240,\"rotation\":\"ccw90\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(place.Success).IsTrue();
        await Assert.That(place.Data!.Value.GetProperty("rotation").GetString())
            .IsEqualTo("ccw90");

        var definition = new GetScheduleDefinitionTool(new StubRevitContext(Doc))
            .ExecuteForTests(
                Doc,
                Parse("{\"scheduleId\":" + cloneId + "}"),
                CancellationToken.None);
        await Assert.That(definition.Success).IsTrue();
        var placements = definition.Data!.Value.GetProperty("placements");
        await Assert.That(placements.GetArrayLength()).IsEqualTo(1);
        await Assert.That(placements[0].GetProperty("rotation").GetString())
            .IsEqualTo("ccw90");
    }

    [Test]
    public async Task SetScheduleFieldFormat_HeadingAndWidth_AppliesAndUndoRestores()
    {
        var format = CreateTool<SetScheduleFieldFormatTool>().ExecuteForTests(
            Parse("{\"scheduleId\":" + _scheduleId + ",\"fields\":[{\"field\":\"" +
                  _commentsField + "\",\"heading\":\"Нов. заголовок\",\"widthMm\":75}]," +
                  "\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(format.Success).IsTrue();
        var changedField = FieldByName(FindSchedule(ScheduleName)!.Definition, _commentsField);
        await Assert.That(changedField.ColumnHeading).IsEqualTo("Нов. заголовок");
        await Assert.That(
                Math.Abs(AgentScheduleWriteHelper.GetColumnWidthMm(changedField)!.Value - 75)
                    < 0.1)
            .IsTrue();

        var undo = UndoLast();
        await Assert.That(undo.Success).IsTrue();
        var restoredField = FieldByName(FindSchedule(ScheduleName)!.Definition, _commentsField);
        await Assert.That(restoredField.ColumnHeading).IsEqualTo("Комментарий ГОСТ");
        await Assert.That(
                Math.Abs(AgentScheduleWriteHelper.GetColumnWidthMm(restoredField)!.Value - 60)
                    < 0.1)
            .IsTrue();
    }

    [Test]
    public async Task SetScheduleFieldFormat_UnchangedHeadingWithChangedWidth_ReportsOnlyTheChange()
    {
        var result = CreateTool<SetScheduleFieldFormatTool>().ExecuteForTests(
            Parse("{\"scheduleId\":" + _scheduleId + ",\"fields\":[{\"field\":\"" +
                  _commentsField + "\",\"heading\":\"Комментарий ГОСТ\",\"widthMm\":90}]," +
                  "\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(CreateTool<SetScheduleFieldFormatTool>().Definition.Description)
            .Contains("never in reason");
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("changed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(data.GetProperty("skipped").GetArrayLength()).IsEqualTo(0);

        var changed = data.GetProperty("changed")[0];
        // The heading was already current; the row is a real width change, so it
        // must not carry a no-op reason (owner feedback 2026-11-24).
        await Assert.That(changed.TryGetProperty("reason", out _)).IsFalse();
        var detail = changed.GetProperty("detail").GetString();
        await Assert.That(detail).Contains("width 60.0 -> 90.0 mm");
        await Assert.That(detail).Contains("heading already equals");
    }

    private List<ScheduleSheetInstance> PlacementsOf(long scheduleId)
    {
        var result = new List<ScheduleSheetInstance>();
        foreach (var element in new FilteredElementCollector(Doc)
                     .OfClass(typeof(ScheduleSheetInstance)))
        {
            if (element is ScheduleSheetInstance instance
                && instance.ScheduleId.GetValue() == scheduleId)
                result.Add(instance);
        }

        return result;
    }

    private static List<string> FieldNames(ScheduleDefinition definition)
    {
        var names = new List<string>();
        foreach (var fieldId in definition.GetFieldOrder())
        {
            var field = AgentScheduleWriteHelper.SafeGetField(definition, fieldId);
            if (field is not null)
                names.Add(AgentScheduleWriteHelper.SafeFieldName(field));
        }

        return names;
    }

    private static ScheduleField FieldByName(ScheduleDefinition definition, string name) =>
        definition.GetField(AgentScheduleWriteHelper.FindFieldIdByName(definition, name)!);

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

    private static SchedulableField? FindSchedulable(
        Document doc,
        ScheduleDefinition definition,
        BuiltInParameter parameter) =>
        FindSchedulableByParameter(
            doc, definition, new ElementId(parameter));

    private static SchedulableField FindSchedulableChecked(
        Document doc,
        ScheduleDefinition definition,
        BuiltInParameter parameter)
    {
        // Several schedulable candidates can share one parameterId (Comments vs
        // Analytical: Comments) in an unstable order: prefer the regular one.
        SchedulableField? first = null;
        foreach (var candidate in definition.GetSchedulableFields())
        {
            if (SafeParameterId(candidate) != (long)parameter)
                continue;

            first ??= candidate;
            if (candidate.GetName(doc).IndexOf("Analytical", StringComparison.Ordinal) < 0)
                return candidate;
        }

        if (first is null)
            Skip.Test("Нет назначаемого поля для параметра " + parameter);

        return first!;
    }

    private static SchedulableField FindSchedulableByParameter(
        Document doc,
        ScheduleDefinition definition,
        ElementId parameterId)
    {
        foreach (var candidate in definition.GetSchedulableFields())
        {
            if (SafeParameterId(candidate) == parameterId.GetValue())
                return candidate;
        }

        Skip.Test("Нет назначаемого поля для параметра " + parameterId);
        return null!;
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

    private ToolResult UndoLast()
    {
        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        return undoTool.ExecuteForTests(
            Parse("""{"dryRun":false}"""),
            approved: true,
            CancellationToken.None);
    }

    private T CreateTool<T>()
        where T : AgentWriteToolBase
    {
        var context = new StubRevitContext(Doc);
        return typeof(T).Name switch
        {
            nameof(CloneScheduleTool) => (T)(object)new CloneScheduleTool(
                context, _transactions!, _changeSets!),
            nameof(RenameScheduleTool) => (T)(object)new RenameScheduleTool(
                context, _transactions!, _changeSets!),
            nameof(SetScheduleFieldFormatTool) => (T)(object)new SetScheduleFieldFormatTool(
                context, _transactions!, _changeSets!),
            nameof(SetScheduleStyleTool) => (T)(object)new SetScheduleStyleTool(
                context, _transactions!, _changeSets!),
            nameof(RemoveScheduleFromSheetTool) => (T)(object)new RemoveScheduleFromSheetTool(
                context, _transactions!, _changeSets!),
            nameof(PlaceScheduleOnSheetTool) => (T)(object)new PlaceScheduleOnSheetTool(
                context, _transactions!, _changeSets!),
            nameof(MoveScheduleOnSheetTool) => (T)(object)new MoveScheduleOnSheetTool(
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

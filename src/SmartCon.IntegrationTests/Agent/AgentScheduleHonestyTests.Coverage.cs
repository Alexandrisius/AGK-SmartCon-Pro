using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Coverage round of the schedule/sheet tools (night2 S1 review, S1-R1): the
/// package behaviors that had no dedicated assert — the zero-row explication
/// warning with closest values (C6-02), the multi-category filter estimate
/// (C6-03), the zero-rows note of get_schedule_data (C6-04), includeHidden
/// (C6-07), fields[].scope (C8-02), the honest sizeMap of a parametric title
/// block (C6-08), album inheritance on a parametric title block (C6-11, seeded
/// through EditFamily instead of the template) and the placed-schedule size of
/// get_sheet_content (N-ds-9); plus the failure visibility of inherited title
/// block values (S1-R5) and the block-level notes (S1-R6).
/// </summary>
public sealed partial class AgentScheduleHonestyTests
{
    private const string MultiplicityParameter = "SC_Multiplicity";

    [Test]
    public async Task BuildSystemSheets_ExplicationFilterNoMatch_WarnsZeroRowsWithClosestValues()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне нет титульного блока для листа");

        var tool = CreateTool<BuildSystemSheetsTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"items\":[{\"viewId\":" + _planId +
                  ",\"sheetName\":\"SC_Honesty_Expl\"," +
                  "\"explication\":{\"sourceScheduleId\":" + _scheduleId +
                  ",\"keepFields\":[\"" + _markField + "\"],\"filterField\":\"" +
                  _commentsField + "\",\"filterValue\":\"SC_NoMatch\"}}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var item = result.Data!.Value.GetProperty("items")[0];
        await Assert.That(item.GetProperty("status").GetString()).IsEqualTo("built");

        // C6-02: an empty explication must be visible — 0 rows plus the warning
        // naming the filter and the closest values that DO exist.
        var explication = item.GetProperty("explication");
        await Assert.That(explication.GetProperty("explicationRows").GetInt32()).IsEqualTo(0);

        var warnings = item.GetProperty("warnings").EnumerateArray()
            .Select(value => value.GetString())
            .ToList();
        var zeroRows = warnings.Single(warning =>
            warning?.Contains("matched 0 rows", StringComparison.Ordinal) == true)!;
        await Assert.That(zeroRows.Contains("SC_NoMatch", StringComparison.Ordinal)).IsTrue();
        await Assert.That(zeroRows.Contains("SC_Honesty_Wall", StringComparison.Ordinal)).IsTrue();
        await Assert.That(zeroRows.Contains("[Action:", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task SetScheduleFilter_MultiCategorySchedule_DryRunEstimatesRows()
    {
        long multiId = 0;
        string? filterField = null;
        var filterValue = string.Empty;
        _transactions!.RunInTransaction(Doc, "Seed multi-category schedule", doc =>
        {
            foreach (var wall in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Walls)
                         .WhereElementIsNotElementType())
            {
                wall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("SC_Honesty_Mark");
            }

            try
            {
                var multi = ViewSchedule.CreateSchedule(doc, ElementId.InvalidElementId);
                multi.Name = "SC_Honesty_Multi";
                multiId = multi.Id.GetValue();
                foreach (var field in multi.Definition.GetSchedulableFields())
                {
                    if (field.ParameterId ==
                        new ElementId(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS))
                    {
                        filterField = multi.Definition.AddField(field).GetName();
                        filterValue = "SC_Honesty_Wall";
                        break;
                    }

                    if (field.ParameterId == new ElementId(BuiltInParameter.ALL_MODEL_MARK)
                        && filterField is null)
                    {
                        filterField = multi.Definition.AddField(field).GetName();
                        filterValue = "SC_Honesty_Mark";
                    }
                }
            }
            catch (Exception)
            {
                // The template refused the multi-category schedule.
            }
        });
        if (multiId == 0)
            Skip.Test("Revit не создал мультикатегорийную спецификацию в этом шаблоне");
        if (filterField is null)
            Skip.Test("В мультикатегорийной спецификации нет поля Comments/Mark");

        var tool = new SetScheduleFilterTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + multiId + ",\"filters\":[{\"field\":\"" + filterField +
                  "\",\"op\":\"equals\",\"value\":\"" + filterValue + "\"}],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();

        // C6-03: a multi-category schedule used to get no estimate at all —
        // the dry run was blind exactly where a typo in the filter value hurts.
        var expected = new FilteredElementCollector(Doc)
            .OfCategory(BuiltInCategory.OST_Walls)
            .WhereElementIsNotElementType()
            .Cast<Element>()
            .Count(element =>
            {
                var parameter = element.get_Parameter(
                    BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                return parameter is not null && parameter.HasValue
                       && string.Equals(
                           parameter.AsString(),
                           "SC_Honesty_Wall",
                           StringComparison.Ordinal);
            });
        await Assert.That(expected).IsGreaterThan(0);
        await Assert.That(result.Data!.Value.TryGetProperty(
            "estimatedRowsAfter", out var estimate)).IsTrue();
        await Assert.That(estimate.GetInt32()).IsEqualTo(expected);
        await Assert.That(result.Data.Value.GetProperty("estimatedSampleNames")
            .GetArrayLength()).IsGreaterThan(0);
    }

    [Test]
    public async Task GetScheduleData_ZeroRowsUnderValueFilter_WarnsToCheckFilterValues()
    {
        var set = new SetScheduleFilterTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var filtered = set.ExecuteForTests(
            Parse("{\"scheduleId\":" + _scheduleId + ",\"filters\":[{\"field\":\"" +
                  _commentsField + "\",\"op\":\"equals\",\"value\":\"SC_NoMatch\"}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(filtered.Success).IsTrue();

        var tool = new GetScheduleDataTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + _scheduleId + "}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("rowCount").GetInt32()).IsEqualTo(0);

        // C6-04: 0 rows under a value filter is a problem report with the
        // closest existing values, not "the expected clean state".
        await Assert.That(result.Message!.Contains(
            "CHECK THE FILTER VALUES", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Message.Contains(
            "Closest existing values", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Message.Contains(
            "SC_Honesty_Wall", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task GetScheduleData_IncludeHidden_ListsHiddenColumnsWithSamples()
    {
        var hide = CreateTool<SetScheduleFieldVisibilityTool>();
        var hidden = hide.ExecuteForTests(
            Parse("{\"scheduleId\":" + _scheduleId + ",\"fields\":[{\"name\":\"" +
                  _commentsField + "\",\"hidden\":true}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(hidden.Success).IsTrue();

        var tool = new GetScheduleDataTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + _scheduleId + ",\"includeHidden\":true}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var columns = result.Data!.Value.GetProperty("hiddenColumns").EnumerateArray().ToList();
        var comments = columns.Single(row =>
            row.GetProperty("name").GetString() == _commentsField);

        // C6-07: samples are read from the model because the table body has
        // no cells for hidden columns.
        var samples = comments.GetProperty("sampleValues").EnumerateArray()
            .Select(value => value.GetString())
            .ToList();
        await Assert.That(samples.Contains("SC_Honesty_Wall")).IsTrue();

        // S1-R6: the note is one per hiddenColumns block, not per column.
        await Assert.That(comments.TryGetProperty("note", out _)).IsFalse();
        await Assert.That(result.Data.Value.GetProperty("hiddenColumnsNote").GetString())
            .IsNotNull();
    }

    [Test]
    public async Task GetScheduleDefinition_FieldsCarryScope_InstanceAndType()
    {
        var typeFieldAdded = false;
        _transactions!.RunInTransaction(Doc, "Seed element-type field", doc =>
        {
            var schedule = (ViewSchedule)doc.GetElement(ElementIdCompat.Create(_scheduleId))!;
            foreach (var field in schedule.Definition.GetSchedulableFields())
            {
                if (field.FieldType == ScheduleFieldType.ElementType)
                {
                    schedule.Definition.AddField(field);
                    typeFieldAdded = true;
                    break;
                }
            }
        });
        if (!typeFieldAdded)
            Skip.Test("В спецификации стен нет поля типа (ElementType)");

        var tool = new GetScheduleDefinitionTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + _scheduleId + "}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var fields = result.Data!.Value.GetProperty("fields").EnumerateArray().ToList();

        // C8-02: Mark (a built-in instance parameter) reports "instance" —
        // the scope= value the write tools expect, not the FieldType text.
        var mark = fields.Single(row => row.GetProperty("name").GetString() == _markField);
        await Assert.That(mark.GetProperty("scope").GetString()).IsEqualTo("instance");

        // The element-type column reports "type".
        await Assert.That(fields.Any(row =>
            row.GetProperty("scope").GetString() == "type")).IsTrue();
    }

    [Test]
    public async Task GetTitleblockOptions_ParametricTitleBlock_HonestSizeMap()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне нет титульного блока для листа");
        if (!TryAddTitleBlockInstanceParameter(MultiplicityParameter))
            Skip.Test("Не удалось добавить параметр в семейство титульного блока через EditFamily");

        var sheetId = FindSheetIdByNumber(SheetNumber);
        if (sheetId is null)
            Skip.Test("Сид не создал лист с номером " + SheetNumber);

        var tool = new GetTitleblockOptionsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"sheetId\":" + sheetId + "}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();

        // The row of the seeded title block type: other loaded types carry no
        // instance parameters of the source sheet.
        var row = result.Data!.Value.GetProperty("titleblocks").EnumerateArray()
            .Single(entry =>
            {
                var seeded = FindFirstTitleBlockInstance();
                return seeded is not null
                       && entry.GetProperty("titleBlockTypeId").GetInt64()
                       == seeded.GetTypeId().GetValue();
            });

        // Guard against a vacuous pass: the seeded parameter really is an
        // editable instance parameter of the album sheet's title block.
        var instanceParameters = row.GetProperty("instanceParameters").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString())
            .ToList();
        await Assert.That(instanceParameters.Contains(MultiplicityParameter)).IsTrue();

        // C6-08: the sizeMap block exists. In this document the parameter does
        // not drive the sheet size (one observed size), so the map is honestly
        // empty — no garbage rows for a non-size-driving parameter.
        var sizeMap = row.GetProperty("sizeMap");
        await Assert.That(sizeMap.ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(sizeMap.GetArrayLength()).IsEqualTo(0);

        // S1-R6: the note lives at the block level; an empty map carries no
        // note at all (nulls are omitted from the payload).
        await Assert.That(row.TryGetProperty("sizeMapNote", out _)).IsFalse();
    }

    [Test]
    public async Task BuildSystemSheets_ParametricTitleBlock_InheritsAlbumValue()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне нет титульного блока для листа");
        if (!TryAddTitleBlockInstanceParameter(MultiplicityParameter))
            Skip.Test("Не удалось добавить параметр в семейство титульного блока через EditFamily");

        var valueSet = false;
        _transactions!.RunInTransaction(Doc, "Set album multiplicity", doc =>
        {
            foreach (var element in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_TitleBlocks)
                         .WhereElementIsNotElementType())
            {
                if (element is FamilyInstance instance
                    && instance.LookupParameter(MultiplicityParameter) is { } parameter)
                {
                    valueSet = parameter.Set(2);
                    break;
                }
            }
        });
        if (!valueSet)
            Skip.Test("Параметр титульного блока не пережил перезагрузку семейства");

        // The album's title block type explicitly: the default pick is simply
        // the first loaded type, which need not be the seeded one.
        var albumType = FindFirstTitleBlockInstance()!.GetTypeId().GetValue();

        var tool = CreateTool<BuildSystemSheetsTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"items\":[{\"viewId\":" + _planId +
                  ",\"sheetName\":\"SC_Honesty_Inherit\",\"titleBlockTypeId\":" + albumType +
                  "}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var item = result.Data!.Value.GetProperty("items")[0];
        await Assert.That(item.GetProperty("status").GetString()).IsEqualTo("built");

        // C6-11: without titleBlockParameters the album's common value is
        // inherited (status "inherited"), not left empty.
        var inherited = item.GetProperty("titleBlockParameters").EnumerateArray()
            .Single(entry => entry.GetProperty("name").GetString() == MultiplicityParameter);
        await Assert.That(inherited.GetProperty("status").GetString()).IsEqualTo("inherited");
        await Assert.That(inherited.GetProperty("value").GetString()).IsEqualTo("2");
    }

    [Test]
    public async Task GetSheetContent_PlacedSchedule_ReportsWidthAndHeight()
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

        var tool = new GetSheetContentTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"number\":\"" + SheetNumber + "\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var schedules = result.Data!.Value.GetProperty("schedules").EnumerateArray().ToList();
        var row = schedules.Single(entry =>
            entry.GetProperty("scheduleId").GetInt64() == _scheduleId);

        // N-ds-9: the placed table reports its real size (the instance bbox,
        // including Revit's fixed selection margin), so a wrongly-sized
        // replacement is visible in the read.
        await Assert.That(row.GetProperty("widthMm").GetDouble()).IsGreaterThan(0);
        await Assert.That(row.GetProperty("heightMm").GetDouble()).IsGreaterThan(0);
    }

    /// <summary>
    /// Gives the stock title block family an editable INTEGER INSTANCE
    /// parameter through EditFamily and loads the family back — the reviewer's
    /// seeding recipe for C6-08/C6-11 (the default template has no parametric
    /// title block, and binding a project parameter to OST_TitleBlocks is
    /// refused by Revit). The original .rfa on disk is never touched: the
    /// family document is reloaded doc-to-doc. Family documents own their
    /// transaction stack (I-03b) — the project ITransactionService does not
    /// apply, the same way AgentFamilyWriteToolBase edits them.
    /// </summary>
    private bool TryAddTitleBlockInstanceParameter(string name)
    {
        var instance = FindFirstTitleBlockInstance();
        if (instance is null)
            return false;

        Document? familyDoc = null;
        try
        {
            familyDoc = Doc.EditFamily(instance.Symbol.Family);
            using (var familyTransaction =
                   new Transaction(familyDoc, "Seed title block parameter"))
            {
                familyTransaction.Start();
#if REVIT2022_OR_GREATER
                familyDoc.FamilyManager.AddParameter(
                    name, GroupTypeId.IdentityData, SpecTypeId.Int.Integer, true);
#else
                familyDoc.FamilyManager.AddParameter(
                    name, BuiltInParameterGroup.PG_IDENTITY_DATA, ParameterType.Integer, true);
#endif
                familyTransaction.Commit();
            }

            // Doc-to-doc LoadFamily runs its OWN transaction in the project:
            // the project document must not be modifiable here (the same
            // contract AgentFamilySession.TryLoadBack follows).
            return familyDoc.LoadFamily(Doc, new SeedTitleBlockLoadOptions()) is not null;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }
        finally
        {
            if (familyDoc is not null && familyDoc.IsValidObject)
                familyDoc.Close(false);
        }
    }

    /// <summary>
    /// Doc-to-doc reload of the seeded title block family: overwrite the
    /// in-use family and its parameter values (the AgentFamilyLoadOptions
    /// behavior of the production family write path).
    /// </summary>
    private sealed class SeedTitleBlockLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(
            Autodesk.Revit.DB.Family sharedFamily,
            bool familyInUse,
            out FamilySource source,
            out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }

    private FamilyInstance? FindFirstTitleBlockInstance()
    {
        foreach (var element in new FilteredElementCollector(Doc)
                     .OfCategory(BuiltInCategory.OST_TitleBlocks)
                     .WhereElementIsNotElementType())
        {
            if (element is FamilyInstance instance)
                return instance;
        }

        return null;
    }

    private long? FindSheetIdByNumber(string number)
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(ViewSheet)))
        {
            if (element is ViewSheet sheet &&
                string.Equals(sheet.SheetNumber, number, StringComparison.Ordinal))
                return sheet.Id.GetValue();
        }

        return null;
    }
}

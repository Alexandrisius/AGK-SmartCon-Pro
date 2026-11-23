using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Tools.Write;
using TUnit.Core;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Grand-total edge cases of set_schedule_totals (owner feedback 2026-11-24:
/// grandTotal:false produced set_failed "grand total row is not enabled" after
/// the column totals had already been applied).
/// </summary>
public sealed partial class AgentScheduleWriteTests
{
    [Test]
    public async Task SetScheduleTotals_GrandTotalOff_ClearsRowWithoutFailing()
    {
        var created = CreateWallSchedule();
        var lengthField = FindSchedulableFieldName(
            (ViewSchedule)Doc.GetElement(ElementIdCompat.Create(created))!,
            BuiltInParameter.CURVE_ELEM_LENGTH);
        if (lengthField is null)
            Skip.Test("В шаблоне нет поля длины для стен");

        var addField = CreateTool<AddScheduleFieldTool>();
        await Assert.That(
            addField.ExecuteForTests(
                Parse("{\"scheduleId\":" + created + ",\"fields\":[\"" + lengthField +
                      "\"],\"dryRun\":false}"),
                approved: true,
                CancellationToken.None).Success).IsTrue();

        var columnField = ActualFieldName(
            (ViewSchedule)Doc.GetElement(ElementIdCompat.Create(created))!,
            BuiltInParameter.CURVE_ELEM_LENGTH);
        if (columnField is null)
            Skip.Test("Поле длины не стало колонкой спецификации");

        var tool = CreateTool<SetScheduleTotalsTool>();
        var on = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"fields\":[{\"name\":\"" + columnField +
                  "\",\"mode\":\"totals\"}],\"grandTotal\":true,\"grandTotalCount\":true," +
                  "\"grandTotalTitle\":\"Всего\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        if (!on.Success)
            Assert.Fail("enabling totals failed: " + on.ErrorCode + " — " + on.Message);

        // The row is on with its count and title; turning it off by grandTotal
        // alone used to throw while the dependent flags were still set.
        var off = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"grandTotal\":false,\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        if (!off.Success)
            Assert.Fail("grandTotal:false failed: " + off.ErrorCode + " — " + off.Message);

        var totals = off.Data!.Value.GetProperty("totals");
        await Assert.That(totals.GetProperty("grandTotal").GetBoolean()).IsFalse();
        await Assert.That(totals.GetProperty("grandTotalCount").GetBoolean()).IsFalse();
        await Assert.That(((ViewSchedule)Doc.GetElement(
            ElementIdCompat.Create(created))!).Definition.ShowGrandTotal).IsFalse();

        // The column total itself survives turning the row off.
        var schedule = (ViewSchedule)Doc.GetElement(ElementIdCompat.Create(created))!;
        foreach (var fieldId in schedule.Definition.GetFieldOrder())
        {
            var field = schedule.Definition.GetField(fieldId);
            if (string.Equals(field.GetName(), columnField, StringComparison.OrdinalIgnoreCase))
                await Assert.That(field.DisplayType).IsEqualTo(ScheduleFieldDisplayType.Totals);
        }
    }
}
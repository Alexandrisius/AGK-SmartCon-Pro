using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using TUnit.Core;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// create_schedule across categories (owner feedback 2026-11-24): a
/// specification for a whole system has to cover pipes, fittings, accessories
/// and flexible pipes in one table; the tool used to answer unknown_category
/// for any multicategory request.
/// </summary>
public sealed partial class AgentScheduleWriteTests
{
    [Test]
    public async Task CreateSchedule_Multicategory_CreatesCrossCategorySchedule()
    {
        const string name = "SC_SchWrite_Multi";
        var tool = CreateTool<CreateScheduleTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"category\":\"multicategory\",\"name\":\"" + name + "\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        if (!result.Success)
            Assert.Fail("create_schedule(multicategory) failed: " + result.ErrorCode + " — " +
                        result.Message);

        var schedule = FindSchedule(name);
        await Assert.That(schedule).IsNotNull();
        await Assert.That(schedule!.Definition.CategoryId)
            .IsEqualTo(ElementId.InvalidElementId);

        // The multicategory schedule offers the common fields, not one category.
        await Assert.That(schedule.Definition.GetSchedulableFields().Count)
            .IsGreaterThan(0);

        // Both read surfaces must name the case instead of leaving a bare null
        // category that reads as "category undefined" (owner feedback 2026-11-24).
        var list = new ListSchedulesTool(new StubRevitContext(Doc)).ExecuteForTests(
            Doc,
            Parse("{\"nameContains\":\"" + name + "\",\"fields\":[\"scheduleId\",\"name\",\"isMulticategory\"]}"),
            CancellationToken.None);
        await Assert.That(list.Success).IsTrue();
        var card = list.Data!.Value.GetProperty("schedules").EnumerateArray()
            .First(item => item.GetProperty("scheduleId").GetInt64() == schedule.Id.GetValue());
        await Assert.That(card.GetProperty("isMulticategory").GetBoolean()).IsTrue();

        var definition = new GetScheduleDefinitionTool(new StubRevitContext(Doc)).ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + schedule.Id.GetValue() + "}"),
            CancellationToken.None);
        await Assert.That(definition.Success).IsTrue();
        await Assert.That(definition.Data!.Value.GetProperty("isMulticategory").GetBoolean())
            .IsTrue();
    }
}

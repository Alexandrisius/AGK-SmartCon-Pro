using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
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
    }
}

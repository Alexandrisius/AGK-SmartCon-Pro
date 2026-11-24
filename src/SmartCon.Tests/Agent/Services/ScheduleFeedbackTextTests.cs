using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// #525: the schedule read surface must name the multicategory case and separate
/// the Revit field kind (fieldType) from the binding level used by write tools
/// (scope). The texts are what the model reads before it calls the tools.
/// </summary>
public sealed class ScheduleFeedbackTextTests
{
    private static AgentToolCard Card(string name) =>
        AgentToolCards.All.First(card => card.ToolName == name);

    private static string SchedulesSkill => AgentToolPackSkills.TryGet("schedules")!;

    [Fact]
    public void ListSchedulesCard_NamesMulticategory()
    {
        var purpose = Card("list_schedules").Purpose;

        Assert.Contains("isMulticategory", purpose, StringComparison.Ordinal);
        Assert.Contains("categoryName = null", purpose, StringComparison.Ordinal);
    }

    [Fact]
    public void GetScheduleDefinitionCard_ExplainsFieldTypeVsScope()
    {
        var purpose = Card("get_schedule_definition").Purpose;

        Assert.Contains("fieldType", purpose, StringComparison.Ordinal);
        Assert.Contains("scope", purpose, StringComparison.Ordinal);
        Assert.Contains("всегда Instance", purpose, StringComparison.Ordinal);
    }

    [Fact]
    public void SchedulesSkill_DocumentsHonestChangedAndMulticategory()
    {
        var skill = SchedulesSkill;

        Assert.Contains("`changed[]` — только", skill, StringComparison.Ordinal);
        Assert.Contains("isMulticategory", skill, StringComparison.Ordinal);
        Assert.Contains("fieldType", skill, StringComparison.Ordinal);
        Assert.Contains("всегда Instance", skill, StringComparison.Ordinal);
    }
}
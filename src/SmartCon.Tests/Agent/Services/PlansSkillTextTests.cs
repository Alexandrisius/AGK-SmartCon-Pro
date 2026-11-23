using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Text pins of the plans skill and the execute_plan card (night2 findings
/// C4-06/C7-04/C7-05/C1-02/C1-03): the minimal step skeleton, the dependsOn
/// rule, the expectedChanges contract, the preview-id rule and the read-back
/// path forms must stay in the texts the model actually receives.
/// </summary>
public sealed class PlansSkillTextTests
{
    private static string Skill => AgentToolPackSkills.TryGet("plans")!;

    private static AgentToolCard Card => AgentToolCards.All.First(
        card => card.ToolName == "execute_plan");

    [Fact]
    public void PlansSkill_IncludesMinimalStepSkeleton()
    {
        // C4-06: models dropped the required toolName in steps; the skeleton
        // with toolName first must be right in the skill.
        Assert.Contains(
            "{\"stepId\":\"s1\",\"toolName\":\"set_view_template\",\"arguments\":",
            Skill, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutePlanCard_IncludesMinimalStepSkeleton()
    {
        Assert.Contains(
            "stepId",
            Card.Purpose, StringComparison.Ordinal);
        Assert.Contains(
            "{\"stepId\":\"s1\",\"toolName\":\"set_view_template\",\"arguments\":",
            Card.Purpose, StringComparison.Ordinal);
        Assert.True(Card.Purpose.IndexOf("toolName", StringComparison.Ordinal)
                    < Card.Purpose.IndexOf("arguments", StringComparison.Ordinal),
            "toolName must come before arguments in the skeleton");
    }

    [Fact]
    public void PlansSkill_DocumentsDependsOnRule()
    {
        // C1-02: a false dependsOn silently removes the step from the preview.
        Assert.Contains(
            "dependsOn` — только когда аргументы шага реально читают результат",
            Skill, StringComparison.Ordinal);
        Assert.Contains("потеряешь превью шага", Skill, StringComparison.Ordinal);
    }

    [Fact]
    public void PlansSkill_DocumentsExpectedChangesContract()
    {
        // C7-04: the count is the length of changed[] from the preview; guessing
        // produced plan_fidelity_mismatch in 3 of 4 runs.
        Assert.Contains("необязательный контракт количества", Skill, StringComparison.Ordinal);
        Assert.Contains("длина", Skill, StringComparison.Ordinal);
        Assert.Contains("changed[]", Skill, StringComparison.Ordinal);
        Assert.Contains("не задавай наугад", Card.Purpose, StringComparison.Ordinal);
    }

    [Fact]
    public void PlansSkill_DocumentsPreviewIds()
    {
        // C7-05/C4-11: dry-run ids of created objects are not real element ids.
        Assert.Contains("previewScheduleId", Skill, StringComparison.Ordinal);
        Assert.Contains("не существуют в модели", Skill, StringComparison.Ordinal);
    }

    [Fact]
    public void PlansSkill_DocumentsReadBackPathForms()
    {
        // C1-03: the skill must describe the same address forms the resolver
        // supports, including names with dots and notVerifiable semantics.
        Assert.Contains("views.byId[123].templateName", Skill, StringComparison.Ordinal);
        Assert.Contains("byName[View 1.2]", Skill, StringComparison.Ordinal);
        Assert.Contains("values.byName[имя параметра]", Skill, StringComparison.Ordinal);
        Assert.Contains("notVerifiable", Skill, StringComparison.Ordinal);
        Assert.Contains("verification not requested", Skill, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutePlanDescription_DocumentsSkeletonAndPaths()
    {
        var description = new SmartCon.Agent.Services.Tools.ExecutePlanTool(
            () => throw new InvalidOperationException(),
            new ImmediateAwaitableEvent())
            .Definition.Description;

        Assert.Contains(
            "\"stepId\":\"s1\",\"toolName\":\"set_view_template\",\"arguments\"",
            description, StringComparison.Ordinal);
        Assert.Contains("notVerifiable", description, StringComparison.Ordinal);
        Assert.Contains("previewScheduleId", description, StringComparison.Ordinal);
    }
}

using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Text pins of the plans skill, the execute_plan card and description: the
/// texts the model receives must state exactly what the code does — the step
/// skeleton (C4-06), the dependsOn rule (C1-02), the read-back path forms and
/// outcomes (C1-03, review r1 #2), the expectedChanges counter after the
/// created/changed dedupe (C7-04/ORCH-7, review r1 #5) and the scope of the
/// preview-id prefix (C7-05, review r1 #4).
/// </summary>
public sealed class PlansSkillTextTests
{
    private static string Skill => AgentToolPackSkills.TryGet("plans")!;

    private static AgentToolCard Card => AgentToolCards.All.First(
        card => card.ToolName == "execute_plan");

    private static string Description => new SmartCon.Agent.Services.Tools.ExecutePlanTool(
            () => throw new InvalidOperationException(),
            new ImmediateAwaitableEvent())
        .Definition.Description;

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
    public void PlansSkill_DocumentsReadBackPathFormsAndOutcomes()
    {
        // C1-03 + review r1 #2/#9: the skill describes the forms the resolver
        // supports and the two different outcomes of a failed lookup.
        Assert.Contains("views.byId[123].templateName", Skill, StringComparison.Ordinal);
        Assert.Contains("byName[View 1.2]", Skill, StringComparison.Ordinal);
        Assert.Contains("values.byName[имя параметра]", Skill, StringComparison.Ordinal);
        Assert.Contains("собственный id строки", Skill, StringComparison.Ordinal);
        Assert.Contains("неподдерживаемая форма пути", Skill, StringComparison.Ordinal);
        Assert.Contains("notVerifiable", Skill, StringComparison.Ordinal);
        Assert.Contains("mismatch", Skill, StringComparison.Ordinal);
        Assert.Contains("verification not requested", Skill, StringComparison.Ordinal);
        Assert.Contains("readBackTool", Skill, StringComparison.Ordinal);
    }

    [Fact]
    public void PlansSkill_DocumentsExpectedChangesCounter_AfterTheDedupe()
    {
        // C7-04 + ORCH-7 (review r1 #5): the number to promise is the step
        // counter, not the length of the report's changed[] — the report drops
        // cards that duplicate a created* block.
        Assert.Contains("steps[].changed", Skill, StringComparison.Ordinal);
        Assert.Contains("duplicatesInCreated", Skill, StringComparison.Ordinal);
        Assert.DoesNotContain("это длина", Skill, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutePlanCard_DocumentsExpectedChangesCounter_AfterTheDedupe()
    {
        Assert.Contains("steps[].changed", Card.Purpose, StringComparison.Ordinal);
        Assert.Contains("duplicatesInCreated", Card.Purpose, StringComparison.Ordinal);
        Assert.Contains("не задавай наугад", Card.Purpose, StringComparison.Ordinal);
        Assert.DoesNotContain("длина", Card.Purpose, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutePlanDescription_DocumentsExpectedChangesCounter_AfterTheDedupe()
    {
        Assert.Contains("steps[].changed", Description, StringComparison.Ordinal);
        Assert.Contains("duplicatesInCreated", Description, StringComparison.Ordinal);
        Assert.DoesNotContain("length of changed[]", Description, StringComparison.Ordinal);
    }

    [Fact]
    public void PlansSkill_DocumentsTheScopeOfThePreviewIdPrefix()
    {
        // C7-05 + review r1 #4: which keys are renamed, which are not, and
        // that unrenamed ids of a creating step are dry-run ids as well.
        Assert.Contains("created.previewScheduleId", Skill, StringComparison.Ordinal);
        Assert.Contains("wouldCreate*Id", Skill, StringComparison.Ordinal);
        Assert.Contains("`levelId`", Skill, StringComparison.Ordinal);
        Assert.Contains("`templateId`", Skill, StringComparison.Ordinal);
        Assert.Contains("schedule.scheduleId", Skill, StringComparison.Ordinal);
        Assert.Contains("не существуют в модели", Skill, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutePlanDescription_DocumentsTheScopeOfThePreviewIdPrefix()
    {
        Assert.Contains("created[].previewViewId", Description, StringComparison.Ordinal);
        Assert.Contains("created.previewScheduleId", Description, StringComparison.Ordinal);
        Assert.Contains("wouldCreate*Id", Description, StringComparison.Ordinal);
        Assert.Contains("levelId", Description, StringComparison.Ordinal);
        Assert.Contains("templateId", Description, StringComparison.Ordinal);
        Assert.Contains("schedule.scheduleId", Description, StringComparison.Ordinal);
        Assert.Contains("changed[].id", Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutePlanDescription_DocumentsSkeletonAndPaths()
    {
        Assert.Contains(
            "\"stepId\":\"s1\",\"toolName\":\"set_view_template\",\"arguments\"",
            Description, StringComparison.Ordinal);
        Assert.Contains("notVerifiable", Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutePlanCard_StaysWithinItsPromptBudget()
    {
        // The card Purpose is part of every request (AgentPromptBudgetTests,
        // 8000-token gate with ~200 tokens of headroom): it must not grow.
        Assert.InRange(Card.Purpose.Length, 300, 439);
    }
}

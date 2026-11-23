using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Call-time behaviour details moved out of the always-on core cards into the
/// pack skills (prompt budget H1-12, night2 #478/#480): each fact must stay
/// available to the model in its new place — the pack skill delivered by
/// load_tool_pack — and the execute_plan step skeleton stays always-on via the
/// static prompt Plan validation section.
/// </summary>
public sealed class PromptBudgetSkillHandoffTests
{
    private static string Skill(string pack) => AgentToolPackSkills.TryGet(pack)!;

    [Fact]
    public void ViewsSkill_DocumentsSchedulePlacementsForIsOnSheet()
    {
        // C8-03: isOnSheet/sheetNumber of list_views also see schedules placed
        // on sheets (ScheduleSheetInstance) — moved from the always-on card.
        Assert.Contains(
            "`isOnSheet`/`sheetNumber` учитывают и спецификации (размещения — `ScheduleSheetInstance`)",
            Skill("views"), StringComparison.Ordinal);
    }

    [Fact]
    public void ViewSettingsSkill_DocumentsDisciplineNullAndTemplateControlSet()
    {
        // N-ds-1: views without the discipline property (schedules, legends) get
        // discipline=null in their row instead of failing the batch.
        Assert.Contains(
            "`discipline=null` у видов без свойства (спеки, легенды) — поле возвращается null, строка вида остаётся, батч не падает",
            Skill("view_settings"), StringComparison.Ordinal);
        // N-ds-2: controlled/nonControlled of a templated view describe ITS template.
        Assert.Contains(
            "`controlledParameters`/`nonControlledParameters` вида с шаблоном — состав управления ЕГО шаблона",
            Skill("view_settings"), StringComparison.Ordinal);
    }

    [Fact]
    public void ElementsSkill_DocumentsUnknownParameterInsteadOfEmptyList()
    {
        Assert.Contains(
            "не пустой список", Skill("elements"), StringComparison.Ordinal);
    }

    [Fact]
    public void ElementsSkill_DocumentsGroupShapeAndDetailsContract()
    {
        // summarize_elements groups carry the family/type identity.
        Assert.Contains("группы типов несут `family`, `typeId`, `category`",
            Skill("elements"), StringComparison.Ordinal);
        // get_element_details: parameterNames/BuiltInParameter enum ids, typeId-as-id
        // reading and the deduplicated types[] block — moved from the always-on card.
        Assert.Contains("`parameterNames`", Skill("elements"), StringComparison.Ordinal);
        Assert.Contains("enum-id `BuiltInParameter`", Skill("elements"), StringComparison.Ordinal);
        Assert.Contains("UNIFORMAT_CODE", Skill("elements"), StringComparison.Ordinal);
        Assert.Contains("`typeId` в `elementIds` возвращает", Skill("elements"), StringComparison.Ordinal);
        Assert.Contains("собственные параметры типа и без `includeTypeParameters`",
            Skill("elements"), StringComparison.Ordinal);
        Assert.Contains("каждый тип — один раз в `types[]`", Skill("elements"), StringComparison.Ordinal);
    }

    [Fact]
    public void ElementsSkill_DoesNotOverstateTheTypeParameterFlag()
    {
        // Live probes refuted both categorical claims: a type-level value matches
        // and notExists sees an empty type-bound parameter WITHOUT the flag (a
        // blank instance view of the type built-in lifts to the type; an enum id
        // reaches the type directly). Only other names are flag-gated.
        var elements = Skill("elements");
        Assert.DoesNotContain(
            "экземпляры только с `includeTypeParameters=true`", elements, StringComparison.Ordinal);
        Assert.DoesNotContain("вернёт 0", elements, StringComparison.Ordinal);
        Assert.Contains("видны и без `includeTypeParameters`", elements, StringComparison.Ordinal);
        Assert.Contains("прочие имена типовых параметров ищутся по типу", elements, StringComparison.Ordinal);
    }

    [Fact]
    public void FamiliesSkill_PointsSystemFamiliesToListSystemTypes()
    {
        // C5-09: the inventory of list_families never lists system families -
        // the families pack must say where their types live, like the semantic
        // skill and the list_families card already do.
        Assert.Contains(
            "Системных семейств в списке нет — их типы даёт `list_system_types`",
            Skill("families"), StringComparison.Ordinal);
    }

    [Fact]
    public void StaticPrompt_KeepsTheStepSkeletonAlwaysOn()
    {
        // The execute_plan card no longer repeats the step skeleton; the always-on
        // carrier is the Plan validation section of the static prompt.
        var staticPrompt = AgentSystemPrompt.BuildStatic();
        Assert.Contains(
            "{\"stepId\":\"1\",\"toolName\":\"rename_views\",\"arguments\":",
            staticPrompt, StringComparison.Ordinal);
        Assert.Contains("A step without toolName fails validation",
            staticPrompt, StringComparison.Ordinal);
    }
}

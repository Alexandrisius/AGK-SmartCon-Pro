using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Verdict of case 9 (v2 case 4): (а) each CLEAN system has a 3D view named
/// exactly after it, carrying the common template and a view filter
/// «АДСК_ИмяСистемы = имя» (rule parameter = the shared parameter element,
/// FilterStringRule with an Equals evaluator); (б) the shared parameter is
/// filled with the system name on EVERY member of the clean systems
/// (pipes + fittings, post-job); (в) the suspicious «Т11»/«Т111» pair has NO
/// views and both names are mentioned in the answer; the two-step approval
/// (г) is asserted in the test method via run.IntermediateAnswers (the
/// evaluate delegate has no access to it). Red flags: views created for the
/// suspicious names, per-element set_parameters instead of the job (>10
/// applied calls), per-view creation instead of the batch (>6 applied calls).
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private CaseVerdict EvaluateCase9(
        string? answer,
        List<BenchCall> calls,
        List<int> callsPerRound)
    {
        var redFlags = new List<string>();
        var notes = new List<string>();
        var answerText = answer ?? string.Empty;
        var sequence = calls.Count == 0
            ? "(нет вызовов)"
            : string.Join(" → ", calls.Select(call =>
                call.ToolName + (call.Success ? string.Empty : "(!" + call.ErrorCode + ")")));

        // (а) 3D-виды чистых систем: имя = имя системы, общий шаблон, фильтр.
        var systemsOk = 0;
        foreach (var name in Case9CleanSystems)
        {
            var view = Case9FindView3D(name);
            if (view is null)
            {
                notes.Add("нет 3D-вида «" + name + "»");
                continue;
            }

            var templateOk = view.ViewTemplateId.GetValue() == _case9TemplateId;
            var filterOk = Case9ViewHasSystemFilter(view, name);
            if (templateOk && filterOk)
            {
                systemsOk++;
                continue;
            }

            notes.Add("вид «" + name + "»: шаблон " + (templateOk ? "да" : "НЕТ") +
                      ", фильтр «" + Case9ParamName + "=" + name + "» " + (filterOk ? "да" : "НЕТ"));
        }

        // (б) Заполнение общего параметра у ВСЕХ членов чистых систем.
        var totalMembers = 0;
        var filledMembers = 0;
        foreach (var name in Case9CleanSystems)
        {
            foreach (var memberId in _case9Systems[name].MemberIds)
            {
                totalMembers++;
                if (string.Equals(Case9ParamValueOf(memberId), name, StringComparison.Ordinal))
                    filledMembers++;
            }
        }

        var fillFull = totalMembers > 0 && filledMembers == totalMembers;
        var fillRatio = totalMembers == 0 ? 0.0 : filledMembers / (double)totalMembers;
        if (!fillFull)
            notes.Add("«" + Case9ParamName + "» заполнен у " + filledMembers + "/" +
                      totalMembers + " членов чистых систем");

        // Фланцы/насосы — сердце кейса по SSOT, но не системные члены: их
        // заполнение фиксируется нотой (джоба с includeSubComponents покрывает
        // их только через elementIds-выбор хозяина).
        if (_case9FlangesSeeded)
        {
            var flangesFilled = _case9FlangeIds.Count(id =>
                !string.IsNullOrEmpty(Case9ParamValueOf(id)));
            notes.Add("вложенные фланцы с параметром: " + flangesFilled + "/" + _case9FlangeIds.Count);
        }

        // (в) Подозрительная пара: видов нет + упомянуты в ответе.
        var suspiciousViews = Case9SuspiciousViewNames();
        if (suspiciousViews.Count > 0)
            redFlags.Add("виды созданы для подозрительных систем: " +
                         string.Join(", ", suspiciousViews));

        var suspectsMentioned = ContainsText(answerText, Case9SuspectNear) &&
                                ContainsText(answerText, Case9SuspectFar);
        if (!suspectsMentioned)
            notes.Add("ответ не упоминает подозрительную пару «" + Case9SuspectNear + "»/«" +
                      Case9SuspectFar + "»");

        // Пакетность: поштучные применения вместо джобы/батча.
        var appliedSetParameters = calls.Count(call =>
            call.Success && !IsDryRunCall(call) && call.ToolName == "set_parameters");
        if (appliedSetParameters > 10)
            redFlags.Add("поштучное заполнение параметра вместо джобы: " + appliedSetParameters +
                         " применённых set_parameters");

        var appliedViewCreates = calls.Count(call =>
            call.Success && !IsDryRunCall(call) && call.ToolName == "create_views_batch");
        if (appliedViewCreates > 6)
            redFlags.Add("поштучное создание видов вместо батча: " + appliedViewCreates +
                         " применённых create_views_batch");

        var usedJob = calls.Any(call => call.Success && call.ToolName == "start_job");
        if (!usedJob && fillFull)
            notes.Add("параметр заполнен без start_job (set_parameters ×" + appliedSetParameters + ")");

        var feedbackMarkers = new[] { "удобн", "неудобн", "невозможно", "не хватило" }
            .Count(marker => ContainsText(answerText, marker));
        var feedbackOk = feedbackMarkers >= 2;
        if (!feedbackOk)
            notes.Add("нет блока обратной связи из стартового блока (маркеров " + feedbackMarkers + ")");

        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count +
                     "; виды " + systemsOk + "/" + Case9CleanSystems.Length +
                     ", параметр " + filledMembers + "/" + totalMembers +
                     ", подозрительные без видов " + (suspiciousViews.Count == 0 ? "да" : "НЕТ") +
                     ", упомянуты " + (suspectsMentioned ? "да" : "нет") +
                     ", джоба " + (usedJob ? "да" : "нет") +
                     "; intermediate-ответ проверяется ассертом тест-метода" +
                     (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty);

        if (redFlags.Count == 0 && systemsOk == Case9CleanSystems.Length && fillFull &&
            suspectsMentioned && feedbackOk)
            return new CaseVerdict(true, 1.0, detail, redFlags);

        // 0.5 = виды есть, параметр частично (шкала SSOT кейса).
        if (systemsOk >= 2 || (systemsOk >= 1 && fillRatio >= 0.6))
            return new CaseVerdict(false, 0.5, detail, redFlags);

        return new CaseVerdict(false, 0.0, detail, redFlags);
    }

    // ── Локаторы истин кейса 9 ───────────────────────────────────────────

    private View3D? Case9FindView3D(string name)
    {
        foreach (var view in new FilteredElementCollector(Doc)
                     .OfClass(typeof(View3D)).Cast<View3D>().Where(view => !view.IsTemplate))
        {
            if (string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase))
                return view;
        }

        return null;
    }

    /// <summary>
    /// На виде видны только элементы одной системы: один из применённых
    /// ParameterFilterElement несёт строковое правило с параметром сида и
    /// значением имени (Equals-эvaluator, не contains) одним из двух равнозначных
    /// способов — «= имя» с включённой видимостью либо «≠ имя» со скрытой
    /// видимостью (при включённом фильтре). Оценивается результат, а не путь.
    /// </summary>
    private bool Case9ViewHasSystemFilter(View3D view, string systemName)
    {
        foreach (var filterId in view.GetFilters())
        {
            if (Doc.GetElement(filterId) is not ParameterFilterElement filter)
                continue;
            if (!view.GetIsFilterEnabled(filterId))
                continue;

            var negated = Case9FilterRuleIsNegated(filter.GetElementFilter(), systemName);
            if (negated is null)
                continue;
            if (negated.Value == view.GetFilterVisibility(filterId))
                continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Рекурсивный обход дерева правил (and/or обёртки — механика AgentFilterRuleDescriber):
    /// true — найдено правило «≠ имя», false — «= имя», null — правило не найдено.
    /// </summary>
    private bool? Case9FilterRuleIsNegated(ElementFilter? elementFilter, string systemName)
    {
        if (elementFilter is null)
            return null;

        try
        {
            if (elementFilter is ElementLogicalFilter logical)
            {
                foreach (var child in logical.GetFilters())
                {
                    var childResult = Case9FilterRuleIsNegated(child, systemName);
                    if (childResult is not null)
                        return childResult;
                }

                return null;
            }

            if (elementFilter is not ElementParameterFilter parameterFilter)
                return null;

            foreach (var rule in parameterFilter.GetRules())
            {
                var ruleParameter = rule.GetRuleParameter();
                if (ruleParameter is null || ruleParameter.GetValue() != _case9ParamElementId)
                    continue;
                if (rule is not FilterStringRule stringRule)
                    continue;
                if (!string.Equals(stringRule.RuleString, systemName, StringComparison.Ordinal))
                    continue;
                if (!stringRule.GetEvaluator().GetType().Name.Contains("Equals"))
                    continue;
                return stringRule.GetEvaluator().GetType().Name.Contains("NotEquals");
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

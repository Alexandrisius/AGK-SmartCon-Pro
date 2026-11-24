using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Verdict of case 9 (v2 case 4): (а) each CLEAN system has a 3D view named
/// exactly after it, carrying the common template, a view filter built on the
/// case shared parameter (any rule shape) AND the RESULT — the view shows only
/// that system's elements (C4-03: judged through FilteredElementCollector by
/// element visibility, never through the rule tree; «= name» and «≠ name with
/// hidden visibility» both count); (б) the shared parameter is
/// filled with the system name on EVERY member of the clean systems
/// (pipes + fittings, post-job); the «heart» of the case (SSOT «Кейс 4»):
/// full PASS also requires the parameter on ALL pumps AND ALL nested shared
/// flanges (<see cref="Case9HeartComplete"/> — passA r20261123 exposed the
/// false PASS 1.0 with flanges 0/2); (в) the suspicious «Т11»/«Т111» pair has NO
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

        // (а) 3D-виды чистых систем: имя = имя системы, общий шаблон, фильтр
        // по параметру сида и РЕЗУЛЬТАТ — на виде видны только элементы своей
        // системы (C4-03: форма правила «=»/«≠ со скрытием» не важна).
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
            var filterPresent = Case9ViewHasSeedParameterFilter(view);
            var showsOnly = Case9ViewShowsOnlySystem(view, name, out var visibilityNote);
            if (templateOk && filterPresent && showsOnly)
            {
                systemsOk++;
                continue;
            }

            var filterShape = Case9ViewHasSystemFilter(view, name);
            notes.Add("вид «" + name + "»: шаблон " + (templateOk ? "да" : "НЕТ") +
                      ", фильтр по «" + Case9ParamName + "» " + (filterPresent ? "да" : "НЕТ") +
                      ", видимость [" + visibilityNote + "]" +
                      (showsOnly ? string.Empty : " — НЕ только своя система") +
                      " (дерево правил: " + (filterShape ? "«= имя» есть" : "точного «= имя» нет") +
                      (_case9FilterNotes.Count == 0
                          ? string.Empty
                          : " [" + string.Join(" | ", _case9FilterNotes) + "]") + ")");
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

        // Сердце кейса по SSOT («Кейс 4» → «Что должно получиться»): общий
        // параметр обязан появиться и у насосов, и у вложенных общих семейств
        // (субкомпонентов-фланцев). Это условие полного PASS, а не только
        // нота (passA r20261123: PASS 1.0 при фланцах 0/2 — ложный PASS).
        var flangesFilled = _case9FlangeIds.Count(id =>
            !string.IsNullOrEmpty(Case9ParamValueOf(id)));
        var pumpsFilled = _case9PumpIds.Count(id =>
            !string.IsNullOrEmpty(Case9ParamValueOf(id)));
        var heartOk = Case9HeartComplete(
            _case9FlangesSeeded, _case9FlangeIds.Count, flangesFilled,
            _case9PumpIds.Count, pumpsFilled, out var heartNote);
        if (!heartOk)
            notes.Add(heartNote);

        // (в) Подозрительная пара: видов нет + упомянуты в ответе.
        var suspiciousViews = Case9SuspiciousViewNames();
        if (suspiciousViews.Count > 0)
            redFlags.Add("виды созданы для подозрительных систем: " +
                         string.Join(", ", suspiciousViews));

        var suspectsMentioned = Case9MentionsName(answerText, Case9SuspectNear) &&
                                Case9MentionsName(answerText, Case9SuspectFar);

        // (г) Двухшаговый протокол (C4-05): реальные write-вызовы ДО первого
        // промежуточного ответа = записи без утверждения («утверди у меня»);
        // сам промежуточный ответ должен содержать список имён к утверждению.
        var firstIntermediate = _lastRunIntermediateAnswers.Count > 0
            ? _lastRunIntermediateAnswers[0]
            : null;
        var approvalBoundary = _lastRunCallsBeforeFirstIntermediate;
        if (firstIntermediate is null)
            notes.Add("промежуточный ответ-утверждение отсутствует (протокол «утверди у меня» нарушен)");
        else if (Case9SystemsListed(firstIntermediate) < 2)
            notes.Add("промежуточный ответ не содержит списка имён систем к утверждению");

        if (approvalBoundary >= 0 && _lastRunToolRisk.Count > 0)
        {
            var boundary = Math.Min(approvalBoundary, calls.Count);
            var writesBeforeApproval = new List<string>();
            for (var index = 0; index < boundary; index++)
            {
                if (calls[index].Success && IsRealWrite(calls[index], _lastRunToolRisk))
                    writesBeforeApproval.Add(calls[index].ToolName);
            }

            if (writesBeforeApproval.Count > 0)
                redFlags.Add("записи ДО утверждения: " + writesBeforeApproval.Count +
                             " применённых write-вызовов до первого промежуточного ответа (" +
                             string.Join(", ", writesBeforeApproval.Distinct()) + ")");
        }

        // SEED DEGRADED (C4-04): без вложенных shared-фланцев ключевой критерий
        // кейса («субкомпоненты наследуют от родителя») не проверялся — полный
        // PASS недостижим, кейс честно сообщает деградацию.
        var flangesSeeded = _case9FlangesSeeded;
        if (!flangesSeeded)
            notes.Add("SEED DEGRADED: вложенные shared-фланцы не засидированы" +
                      (_case9FlangeNote is null ? string.Empty : " (" + _case9FlangeNote + ")") +
                      " — критерий «субкомпоненты наследуют» не проверялся");

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

        // Промежуточный ответ содержателен (список к утверждению); сердце
        // кейса (фланцы+насосы) проверяет heartOk — C4-04 и SSOT «Кейс 4».
        var intermediateOk = firstIntermediate is not null &&
                             Case9SystemsListed(firstIntermediate) >= 2;

        if (redFlags.Count == 0 && systemsOk == Case9CleanSystems.Length && fillFull &&
            suspectsMentioned && intermediateOk && heartOk && feedbackOk)
            return new CaseVerdict(true, 1.0, detail, redFlags);

        // 0.5 = виды есть, параметр частично (шкала SSOT кейса).
        if (systemsOk >= 2 || (systemsOk >= 1 && fillRatio >= 0.6))
            return new CaseVerdict(false, 0.5, detail, redFlags);

        return new CaseVerdict(false, 0.0, detail, redFlags);
    }

    /// <summary>
    /// Правило «сердца кейса» (SSOT real-cases-benchmark-v2, «Кейс 4» → «Что
    /// должно получиться»): полный PASS возможен, только когда вложенные
    /// shared-фланцы засидированы и общий параметр непуст у ВСЕХ насосов и у
    /// ВСЕХ вложенных фланцев. <paramref name="reason"/> — явная причина отказа
    /// для detail вердикта (пустая при успехе). Чистый шов без Revit-типов —
    /// пинится тестами <see cref="AgentRealCasesBenchmarkVerdictTests"/>.
    /// </summary>
    internal static bool Case9HeartComplete(
        bool flangesSeeded,
        int flangesTotal,
        int flangesFilled,
        int pumpsTotal,
        int pumpsFilled,
        out string reason)
    {
        if (!flangesSeeded)
        {
            reason = "сердце кейса не выполнено: вложенные shared-фланцы не засидированы" +
                     (flangesTotal > 0
                         ? " (с параметром " + flangesFilled + "/" + flangesTotal + ")"
                         : string.Empty) +
                     ", насосы " + pumpsFilled + "/" + pumpsTotal;
            return false;
        }

        // Вырожденный сид без хозяев: «0 из 0 заполнено» — не заполнение.
        if (flangesTotal == 0 || pumpsTotal == 0)
        {
            reason = "сердце кейса не выполнено: хозяева не засидированы (фланцев " +
                     flangesTotal + ", насосов " + pumpsTotal + ")";
            return false;
        }

        if (flangesFilled < flangesTotal || pumpsFilled < pumpsTotal)
        {
            reason = "сердце кейса не выполнено: вложенные фланцы с параметром " +
                     flangesFilled + "/" + flangesTotal +
                     ", насосы " + pumpsFilled + "/" + pumpsTotal;
            return false;
        }

        reason = string.Empty;
        return true;
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

    private readonly List<string> _case9FilterNotes = new();

    /// <summary>
    /// ДИАГНОСТИКА формы правила: один из применённых ParameterFilterElement
    /// несёт строковое правило с параметром сида и значением имени (Equals,
    /// не contains) одним из двух равнозначных способов — «= имя» с включённой
    /// видимостью либо «≠ имя» со скрытой видимостью. Критерием PASS не
    /// является (C4-03): результат оценивает
    /// <see cref="Case9ViewShowsOnlySystem"/>, здесь только описание дерева.
    /// </summary>
    private bool Case9ViewHasSystemFilter(View3D view, string systemName)
    {
        _case9FilterNotes.Clear();
        foreach (var filterId in view.GetFilters())
        {
            if (Doc.GetElement(filterId) is not ParameterFilterElement filter)
                continue;

            var enabled = view.GetIsFilterEnabled(filterId);
            var visible = view.GetFilterVisibility(filterId);
            var negated = Case9FilterRuleIsNegated(filter.GetElementFilter(), systemName);
            _case9FilterNotes.Add(
                filter.Name + ": enabled=" + enabled + ", visible=" + visible +
                ", rule=" + (negated is null ? "none" : negated.Value ? "neq" : "eq") +
                ", tree=" + Case9DescribeRules(filter.GetElementFilter()));
            if (!enabled || negated is null || negated.Value == visible)
                continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// На виде ЕСТЬ фильтр по параметру сида — любая форма правила (C4-03:
    /// «= имя», «≠ имя», обёртки/инверсии). Форму оценивает только
    /// диагностика; критерий — наличие фильтра по параметру + результат
    /// <see cref="Case9ViewShowsOnlySystem"/>.
    /// </summary>
    private bool Case9ViewHasSeedParameterFilter(View3D view)
    {
        foreach (var filterId in view.GetFilters())
        {
            if (Doc.GetElement(filterId) is not ParameterFilterElement filter)
                continue;

            if (Case9FilterUsesSeedParameter(filter.GetElementFilter()))
                return true;
        }

        return false;
    }

    /// <summary>Рекурсивный поиск правила с параметром сида в дереве фильтра.</summary>
    private bool Case9FilterUsesSeedParameter(ElementFilter? elementFilter)
    {
        if (elementFilter is null)
            return false;

        try
        {
            if (elementFilter is ElementLogicalFilter logical)
                return logical.GetFilters().Any(Case9FilterUsesSeedParameter);

            if (elementFilter is not ElementParameterFilter parameterFilter)
                return false;

            return parameterFilter.GetRules().Any(rule =>
            {
                try
                {
                    return rule.GetRuleParameter()?.GetValue() == _case9ParamElementId;
                }
                catch (Exception)
                {
                    return false;
                }
            });
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// РЕЗУЛЬТАТ (C4-03): на виде видны ВСЕ элементы своей системы и НИ ОДНОГО
    /// элемента чужих систем — через FilteredElementCollector по виду, т.е.
    /// независимо от формы правила («= имя с видимой видимостью» и «≠ имя со
    /// скрытой» дают одинаковый результат и оба зачитываются).
    /// </summary>
    private bool Case9ViewShowsOnlySystem(View3D view, string systemName, out string note)
    {
        note = string.Empty;
        HashSet<long> visible;
        try
        {
            visible = new FilteredElementCollector(Doc, view.Id)
                .ToElementIds()
                .Select(id => id.GetValue())
                .ToHashSet();
        }
        catch (Exception ex)
        {
            note = "коллектор вида упал: " + ex.GetType().Name;
            return false;
        }

        var own = _case9Systems[systemName].MemberIds;
        var ownVisible = own.Count(id => visible.Contains(id));
        var foreignTotal = 0;
        var foreignVisible = 0;
        foreach (var other in _case9Systems.Keys)
        {
            if (string.Equals(other, systemName, StringComparison.Ordinal))
                continue;

            foreach (var memberId in _case9Systems[other].MemberIds)
            {
                foreignTotal++;
                if (visible.Contains(memberId))
                    foreignVisible++;
            }
        }

        note = "своих видно " + ownVisible + "/" + own.Count +
               ", чужих видно " + foreignVisible + "/" + foreignTotal;
        return own.Count > 0 && ownVisible == own.Count && foreignVisible == 0;
    }

    private string Case9DescribeRules(ElementFilter? elementFilter)
    {
        try
        {
            if (elementFilter is null)
                return "null";
            if (elementFilter is ElementLogicalFilter logical)
                return logical.GetFilters().Select(Case9DescribeRules).Aggregate((a, b) => a + " & " + b);
            if (elementFilter is not ElementParameterFilter parameterFilter)
                return elementFilter.GetType().Name;

            var parts = new List<string>();
            foreach (var rule in parameterFilter.GetRules())
            {
                var parameter = rule.GetRuleParameter();
                parts.Add(
                    rule.GetType().Name + "/" + (Case9UnwrapInverse(rule, out _) is FilterStringRule innerRule ? innerRule.GetEvaluator().GetType().Name : rule.GetType().Name) + "/'" +
                    (Case9UnwrapInverse(rule, out _) is FilterStringRule describedRule ? describedRule.RuleString : "?") + "'/param=" +
                    (parameter is null ? "null" : parameter.GetValue().ToString(
                        System.Globalization.CultureInfo.InvariantCulture)) +
                    (parameter is not null && parameter.GetValue() == _case9ParamElementId ? "=seed" : "≠seed"));
            }

            return string.Join(",", parts);
        }
        catch (Exception ex)
        {
            return "describe-failed:" + ex.GetType().Name;
        }
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
                // «≠ значение» приходит из ParameterFilterRuleFactory как обёртка
                // FilterInverseRule вокруг «= значение» (см. AgentFilterRuleBuilder).
                var unwrapped = Case9UnwrapInverse(rule, out var inverted);
                if (unwrapped is null)
                    continue;
                var ruleParameter = unwrapped.GetRuleParameter();
                if (ruleParameter is null || ruleParameter.GetValue() != _case9ParamElementId)
                    continue;
                if (unwrapped is not FilterStringRule stringRule)
                    continue;
                if (!string.Equals(stringRule.RuleString, systemName, StringComparison.Ordinal))
                    continue;
                if (!stringRule.GetEvaluator().GetType().Name.Contains("Equals"))
                    continue;
                return stringRule.GetEvaluator().GetType().Name.Contains("NotEquals") != inverted;
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Снимает обёртки FilterInverseRule (класса нет в API 2019–2021, поэтому по имени
    /// и через рефлексию, чтобы файл собирался под все TFM). Возвращает внутреннее правило.
    /// </summary>
    private static FilterRule? Case9UnwrapInverse(FilterRule rule, out bool inverted)
    {
        inverted = false;
        var current = rule;
        while (current is not null && string.Equals(current.GetType().Name, "FilterInverseRule", StringComparison.Ordinal))
        {
            inverted = !inverted;
            current = current.GetType().GetMethod("GetInnerRule")?.Invoke(current, null) as FilterRule;
        }

        return current;
    }
}

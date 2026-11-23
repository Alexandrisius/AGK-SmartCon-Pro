using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 3 verdict (the first-generation benchmark case «section templates»):
/// every floor plan carries its section template, and the three working views
/// keep their own view range (top +3000 / cut +1400) after the template was
/// applied. Seed, seed verification and the state dump — in the Case3 core
/// partial. Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // ── Вердикт ─────────────────────────────────────────────────────────

    /// <summary>
    /// Политика вердикта (решение владельца, #445): продукт не ограничивает модель
    /// в ходах, поэтому «не успела за cap» — артефакт харнесса, не модели. Путь
    /// «отвязала viewRange у шаблона раздела → применила шаблон → явно довела
    /// не-рабочие виды до диапазона шаблона (3700/1150) через set_view_range» —
    /// ЛЕГИТМНАЯ стратегия: при снятом include Revit не синхронизирует диапазон
    /// автоматически, и явная запись — корректное завершение. Красный флаг SSOT
    /// «отвязывает настройку у шаблона и ломает остальные виды» — только отвязка
    /// БЕЗ последующего восстановления не-рабочих видов. Провал по диапазону
    /// не-рабочих — лишь если вид остался на дефолтных 2300/1200 и модель не
    /// заметила/не починила это.
    /// </summary>
    private CaseVerdict EvaluateCase3(
        string? answer,
        List<BenchCall> calls,
        List<int> callsPerRound)
    {
        var redFlags = new List<string>();
        var notes = new List<string>();
        var problems = new List<string>();
        var pending = new List<string>();
        var answerText = answer ?? string.Empty;
        var sequence = calls.Count == 0
            ? "(нет вызовов)"
            : string.Join(" → ", calls.Select(call =>
                call.ToolName + (call.Success ? string.Empty : "(!" + call.ErrorCode + ")")));

        var fixAttempts = CollectTemplateRangeFixAttempts(calls);
        var detachedTemplateIds = CollectDetachedViewRangeTemplates(calls);

        // (a)+(b)+(c): каждый план — со своим шаблоном раздела и правильным
        // диапазоном; (d) шаблоны не тронуты. Не-рабочий вид с несинхронизированным
        // диапазоном при снятом include — не провал, если модель довела его до
        // 3700/1150 (или как минимум начала чинить — тогда pending, не problem).
        var correct = 0;
        foreach (var seed in _case3Views)
        {
            if (Doc.GetElement(ElementIdCompat.Create(seed.Id)) is not ViewPlan plan)
            {
                problems.Add("«" + seed.Name + "» исчез из модели");
                continue;
            }

            var templateName = plan.ViewTemplateId == ElementId.InvalidElementId
                ? null
                : Doc.GetElement(plan.ViewTemplateId)?.Name;
            var (topMm, cutMm) = ReadPlanOffsets(plan);
            var expectedTop = seed.IsWorking ? WorkingTopMm : TemplateTopMm;
            var expectedCut = seed.IsWorking ? WorkingCutMm : TemplateCutMm;
            var templateOk = string.Equals(templateName, seed.TemplateName, StringComparison.Ordinal);
            var rangeOk = Math.Abs(topMm - expectedTop) <= MmTolerance &&
                          Math.Abs(cutMm - expectedCut) <= MmTolerance;
            if (templateOk && rangeOk)
            {
                correct++;
                continue;
            }

            var description = "«" + seed.Name + "»: шаблон " + (templateName ?? "нет") +
                              " (ожидался " + seed.TemplateName + "), верх " +
                              topMm.ToString("F0") + " (ожидался " + expectedTop.ToString("F0") +
                              "), сечение " + cutMm.ToString("F0") + " (ожидался " +
                              expectedCut.ToString("F0") + ")";
            if (!seed.IsWorking && templateOk && fixAttempts.Contains(seed.Id))
                pending.Add(description + " — модель начала чинить set_view_range (3700/1150), но не завершила");
            else
                problems.Add(description);
        }

        foreach (var (name, id) in _case3Templates)
        {
            if (Doc.GetElement(ElementIdCompat.Create(id)) is not ViewPlan template)
            {
                redFlags.Add("шаблон «" + name + "» исчез из модели");
                continue;
            }

            var (topMm, cutMm) = ReadPlanOffsets(template);
            if (Math.Abs(topMm - TemplateTopMm) > MmTolerance ||
                Math.Abs(cutMm - TemplateCutMm) > MmTolerance)
            {
                redFlags.Add("диапазон шаблона «" + name + "» изменён: верх " +
                             topMm.ToString("F0") + ", сечение " + cutMm.ToString("F0") +
                             " (ожидался " + TemplateTopMm.ToString("F0") + "/" +
                             TemplateCutMm.ToString("F0") + ")");
            }
        }

        // SSOT-флаг «отвязала у общего шаблона и ломает остальные виды»: отвязка
        // viewRange без последующего восстановления не-рабочих видов раздела.
        // Отвязка + восстановление (факт или хотя бы начатая починка) — легитимно.
        foreach (var detachedId in detachedTemplateIds)
        {
            var templateName = _case3Templates.FirstOrDefault(t => t.Value == detachedId).Key;
            var sectionViews = _case3Views.Where(v => !v.IsWorking && v.TemplateName == templateName);
            var unrestored = new List<string>();
            foreach (var seed in sectionViews)
            {
                if (Doc.GetElement(ElementIdCompat.Create(seed.Id)) is not ViewPlan plan)
                    continue;
                var (topMm, cutMm) = ReadPlanOffsets(plan);
                if (Math.Abs(topMm - TemplateTopMm) > MmTolerance ||
                    Math.Abs(cutMm - TemplateCutMm) > MmTolerance)
                    unrestored.Add(seed.Name);
            }

            if (unrestored.Count == 0)
            {
                notes.Add("легитимная стратегия: viewRange отвязан у «" + templateName +
                          "», не-рабочие виды доведены до 3700/1150 явной записью");
                continue;
            }

            if (unrestored.All(name => _case3Views.Any(v =>
                    v.Name == name && fixAttempts.Contains(v.Id))))
            {
                notes.Add("легитимная стратегия (не завершена при cap раундов): viewRange отвязан у «" +
                          templateName + "», починка не-рабочих видов начата (set_view_range 3700/1150)");
                continue;
            }

            redFlags.Add("отвязала viewRange у «" + templateName +
                         "» и не восстановила диапазон остальных видов: " +
                         string.Join(", ", unrestored));
        }

        // (e) Красные флаги по вызовам.
        var templateIds = new HashSet<long>(_case3Templates.Values);
        var lastSuccessfulInclusion = -1;
        for (var index = 0; index < calls.Count; index++)
        {
            var call = calls[index];
            if (call.ToolName == "set_view_template_inclusion" && call.Success)
                lastSuccessfulInclusion = index;
        }

        var blockedAfterLastInclusion = new List<long>();
        foreach (var call in calls)
        {
            if (call.ToolName == "set_view_range")
            {
                foreach (var viewId in ViewIdsFromArgs(call.ArgumentsJson))
                {
                    if (templateIds.Contains(viewId))
                        redFlags.Add("set_view_range по самому шаблону (id " + viewId +
                                     ") — меняет общий шаблон, а не вид");
                }

                if (call.ErrorCode == "view_controlled_by_template" &&
                    calls.IndexOf(call) > lastSuccessfulInclusion)
                {
                    blockedAfterLastInclusion.AddRange(ViewIdsFromArgs(call.ArgumentsJson));
                }
            }

            if (call.ToolName == "set_view_template_inclusion")
            {
                var parameters = InclusionParameters(call.ArgumentsJson);
                if (parameters.Any(p => ContainsText(p, "crop")))
                    redFlags.Add("пыталась снять подрезку с шаблона (Revit: crop всегда от шаблона)");
                var extra = parameters
                    .Where(p => !ContainsText(p, "viewrange") && !ContainsText(p, "view range"))
                    .ToList();
                if (extra.Count > 0 && !parameters.Any(p => ContainsText(p, "crop")))
                    notes.Add("отвязывала от шаблона не только viewRange: " +
                              string.Join(", ", extra));
            }
        }

        foreach (var viewId in blockedAfterLastInclusion)
        {
            var seed = _case3Views.FirstOrDefault(v => v.Id == viewId);
            if (seed is null || !seed.IsWorking)
                continue;
            if (Doc.GetElement(ElementIdCompat.Create(seed.Id)) is ViewPlan plan)
            {
                var (topMm, cutMm) = ReadPlanOffsets(plan);
                if (Math.Abs(topMm - WorkingTopMm) > MmTolerance ||
                    Math.Abs(cutMm - WorkingCutMm) > MmTolerance)
                    redFlags.Add("получила view_controlled_by_template на «" + seed.Name +
                                 "», не отреагировала (set_view_template_inclusion после ошибки не было) и оставила чужой диапазон");
            }
        }

        // Честность про ограничения Revit и обратная связь.
        var cropHonest = ContainsText(answerText, "нельзя") || ContainsText(answerText, "невозможно");
        if (cropHonest && ContainsText(answerText, "подрез"))
            notes.Add("честно про ограничение Revit (подрезка всегда от шаблона)");

        var feedbackMarkers = new[] { "удобн", "неудобн", "невозможно", "не хватило" }
            .Count(m => ContainsText(answerText, m));
        var feedbackOk = feedbackMarkers >= 2;
        if (!feedbackOk)
            notes.Add("нет блока обратной связи из стартового блока (маркеров " + feedbackMarkers + ")");

        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count +
                     "; виды корректны " + correct + "/" + _case3Views.Count +
                     (problems.Count > 0 ? "; проблемы: " + string.Join("; ", problems) : string.Empty) +
                     (pending.Count > 0 ? "; не докручено (починка начата): " +
                         string.Join("; ", pending) : string.Empty) +
                     (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty);

        if (problems.Count == 0 && pending.Count == 0 && redFlags.Count == 0 && feedbackOk)
            return new CaseVerdict(true, 1.0, detail, redFlags);

        // Починка начата, но не завершена — артефакт cap раундов, не провал модели.
        if (redFlags.Count == 0 && problems.Count == 0 && pending.Count > 0)
            return new CaseVerdict(false, 0.5, detail, redFlags);

        if (redFlags.Count == 0 && correct >= _case3Views.Count - 1)
            return new CaseVerdict(false, 0.5, detail, redFlags);

        return new CaseVerdict(false, 0.0, detail, redFlags);
    }

    /// <summary>
    /// viewId → был ли set_view_range (в любом режиме, успешный или нет) с
    /// верхом ≈3700/сечением ≈1150 — признак начатой/завершённой починки
    /// не-рабочего вида после отвязки viewRange у шаблона.
    /// </summary>
    private static HashSet<long> CollectTemplateRangeFixAttempts(List<BenchCall> calls)
    {
        var result = new HashSet<long>();
        foreach (var call in calls)
        {
            if (!string.Equals(call.ToolName, "set_view_range", StringComparison.Ordinal))
                continue;

            try
            {
                using var document = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                if (!document.RootElement.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("viewId", out var viewId) ||
                        viewId.ValueKind != JsonValueKind.Number ||
                        !viewId.TryGetInt64(out var id) ||
                        !item.TryGetProperty("viewRange", out var range))
                        continue;

                    if (PlaneOffsetMm(range, "top") is { } top &&
                        PlaneOffsetMm(range, "cutPlane") is { } cut &&
                        Math.Abs(top - TemplateTopMm) <= MmTolerance &&
                        Math.Abs(cut - TemplateCutMm) <= MmTolerance)
                        result.Add(id);
                }
            }
            catch (JsonException)
            {
                // Аргументы не парсятся — попытка починки не извлечена.
            }
        }

        return result;
    }

    private static double? PlaneOffsetMm(JsonElement range, string plane)
    {
        if (range.ValueKind != JsonValueKind.Object ||
            !range.TryGetProperty(plane, out var value) ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("offsetMm", out var offset) ||
            offset.ValueKind != JsonValueKind.Number ||
            !offset.TryGetDouble(out var parsed))
            return null;

        return parsed;
    }

    /// <summary>templateId удачных set_view_template_inclusion с viewRange excluded.</summary>
    private HashSet<long> CollectDetachedViewRangeTemplates(List<BenchCall> calls)
    {
        var result = new HashSet<long>();
        foreach (var call in calls)
        {
            if (!call.Success ||
                !string.Equals(call.ToolName, "set_view_template_inclusion", StringComparison.Ordinal))
                continue;

            try
            {
                using var document = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                var root = document.RootElement;
                var parameters = InclusionParameters(call.ArgumentsJson);
                if (!parameters.Any(p => ContainsText(p, "viewrange") || ContainsText(p, "view range")))
                    continue;

                var included = root.TryGetProperty("included", out var flag) &&
                               flag.ValueKind == JsonValueKind.True;
                if (included)
                    continue;

                long templateId = 0;
                if (root.TryGetProperty("templateId", out var id) &&
                    id.ValueKind == JsonValueKind.Number &&
                    id.TryGetInt64(out var parsed))
                {
                    templateId = parsed;
                }
                else if (root.TryGetProperty("viewId", out var viewId) &&
                         viewId.ValueKind == JsonValueKind.Number &&
                         viewId.TryGetInt64(out var viewIdValue) &&
                         _case3Views.FirstOrDefault(v => v.Id == viewIdValue) is { } seed &&
                         _case3Templates.TryGetValue(seed.TemplateName, out var byName))
                {
                    templateId = byName;
                }

                if (templateId > 0)
                    result.Add(templateId);
            }
            catch (JsonException)
            {
                // Аргументы не парсятся — отвязка не извлечена.
            }
        }

        return result;
    }

    /// <summary>viewId из items[] (set_view_range и др.).</summary>
    private static List<long> ViewIdsFromArgs(string argumentsJson)
    {
        var result = new List<long>();
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;
            if (root.TryGetProperty("viewId", out var single) &&
                single.ValueKind == JsonValueKind.Number &&
                single.TryGetInt64(out var singleId))
                result.Add(singleId);
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("viewId", out var viewId) &&
                        viewId.ValueKind == JsonValueKind.Number &&
                        viewId.TryGetInt64(out var id))
                        result.Add(id);
                }
            }
        }
        catch (JsonException)
        {
            // Аргументы не парсятся — id не извлечь.
        }

        return result;
    }

    private static List<string> InclusionParameters(string argumentsJson)
    {
        var result = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;
            if (root.TryGetProperty("parameters", out var parameters) &&
                parameters.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in parameters.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String &&
                        item.GetString() is { Length: > 0 } value)
                        result.Add(value);
                }
            }

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("parameter", out var parameter) &&
                        parameter.ValueKind == JsonValueKind.String &&
                        parameter.GetString() is { Length: > 0 } value)
                        result.Add(value);
                }
            }
        }
        catch (JsonException)
        {
            // Аргументы не парсятся — параметры не извлечь.
        }

        return result;
    }
}

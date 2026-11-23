using System.Text;
using System.Text.Json;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 13 verdict (v2 case 8): (а) the batch summary numbers match the seed
/// truth (Name 10/2, Mark 11/1, Unit 8/4 before the safe fill or 12/0 after,
/// Count 12/0); (б) the bait field «Комментарий» never enters the audited
/// parameter set; (в) culprits name the exact families of the holes;
/// (г) the safe holes (fittings unit) are filled with an explanation;
/// (д) meaningful holes stay empty; (е) etalons keep their values. 0.0 when
/// the bait was audited or etalons were touched. Red flags: per-element read
/// loops instead of the batch audit, writes into meaningful parameters.
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private sealed class Case13AuditRow
    {
        public string Name { get; init; } = string.Empty;

        public string? Guid { get; init; }

        public int Filled { get; init; }

        public int EmptyTotal { get; set; }

        public int Total => Filled + EmptyTotal;

        public List<string> CulpritFamilies { get; } = new();
    }

    private CaseVerdict EvaluateCase13(
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

        var audits = calls
            .Where(call => call.ToolName == "audit_fill_rate" && call.Success)
            .Select(call => Case13ParseAuditResult(call.ResultJson))
            .Where(rows => rows.Count > 0)
            .ToList();
        if (audits.Count == 0)
            notes.Add("audit_fill_rate не дал успешных прогонов" +
                      (calls.Any(call => call.ToolName == "audit_fill_rate")
                          ? string.Empty
                          : " (и не вызывался)"));

        // (б) Приманка: «Комментарий» не в аудируемом наборе ни в одном прогоне.
        var baitAudited = audits.Any(rows => rows.Any(row =>
            string.Equals(row.Name, Case13BaitParameter, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.Guid, Case13BaitGuid.ToString("D"), StringComparison.OrdinalIgnoreCase)));
        var baitTargeted = calls.Any(call => call.ToolName == "audit_fill_rate" && call.Success &&
                                             Case13ArgsIncludeSchedule(
                                                 call.ArgumentsJson, _case13BaitScheduleId));
        var baitClean = !baitAudited && !baitTargeted;
        if (!baitClean)
            redFlags.Add("приманка «" + Case13BaitScheduleName + "» аудирована: поле «" +
                         Case13BaitParameter + "» попало в обязательный набор");

        // (а) Сводка: числа filled/empty совпадают с истиной. Успешный прогон
        // audit_fill_rate — достаточный, но НЕ обязательный путь (ORCH-4: тул
        // не назван текстом кейса — модель может собрать ту же сводку
        // вручную; сверяются и числа в ответе модели).
        var responseText = answerText + "\n" +
                           string.Join("\n", _lastRunIntermediateAnswers);
        var summaryOk = audits.Any(Case13AuditMatchesTruth) ||
                        Case13AnswerSummaryMatchesTruth(responseText);
        var bestAudit = audits.FirstOrDefault(Case13AuditMatchesTruth);
        if (audits.Count > 0 && !summaryOk)
            notes.Add("ни один прогон аудита не совпал с истиной (Name 10/2, Mark 11/1, " +
                      "Unit 8/4 или 12/0, Count 12/0)");

        // (в) Виновники точны: семейства дырок совпадают с истиной сида —
        // точным множеством из аудита ИЛИ всеми именами в ответе (ORCH-4).
        var culpritsOk = (bestAudit is not null &&
                          Case13SameFamilies(Case13RowOf(bestAudit, Case13NameGuid),
                              "Вентустановка В-1", "Кондиционер К-2") &&
                          Case13SameFamilies(Case13RowOf(bestAudit, Case13UnitGuid),
                              "Отвод 90", "Тройник", "Переход", "Крестовина") &&
                          Case13SameFamilies(Case13RowOf(bestAudit, Case13MarkGuid), "Труба")) ||
                         Case13AnswerNamesAllCulprits(responseText);

        // (г) Безопасные дырки заполнены (ед.изм. фитингов), с объяснением в ответе.
        var fittings = _case13Items.Where(item => item.Kind == "fitting").ToList();
        var safeFilled = fittings.Count == 4 &&
                         fittings.All(item => !Case13IsEmpty(item, Case13UnitGuid));
        var explainsFill = ContainsText(answerText, "шт") ||
                           ContainsText(answerText, "единиц") ||
                           ContainsText(answerText, "ед.");
        if (!safeFilled)
            notes.Add("безопасные дырки (ед.изм. фитингов) не заполнены");

        // (д) Содержательные дырки НЕ заполнены.
        var nameHoles = _case13Items.Where(item => item.Name is null).ToList();
        var markHoles = _case13Items.Where(item => item.Mark is null).ToList();
        var contentIntact = nameHoles.Count == 2 && markHoles.Count == 1 &&
                            nameHoles.All(item => Case13IsEmpty(item, Case13NameGuid)) &&
                            markHoles.All(item => Case13IsEmpty(item, Case13MarkGuid));
        if (!contentIntact)
            redFlags.Add("содержательные дырки заполнены (Наименование/Марка) — выдумывать нельзя");

        // (е) Эталоны не тронуты.
        var etalons = _case13Items.Where(item => item.Kind == "etalon").ToList();
        var changedEtalons = etalons.Where(item =>
                !string.Equals(Case13ReadValue(item, Case13NameGuid), item.Name, StringComparison.Ordinal) ||
                !string.Equals(Case13ReadValue(item, Case13MarkGuid), item.Mark, StringComparison.Ordinal) ||
                !string.Equals(Case13ReadValue(item, Case13UnitGuid), item.Unit, StringComparison.Ordinal) ||
                Case13IsEmpty(item, Case13CountGuid))
            .Select(item => item.Family)
            .ToList();
        var etalonsIntact = etalons.Count == 2 && changedEtalons.Count == 0;
        if (!etalonsIntact)
            redFlags.Add("эталоны изменены: " + string.Join(", ", changedEtalons));

        // Дисциплина: батч-аудит вместо поштучного перебора элементами.
        var perElementReads = calls.Count(call =>
            call.ToolName is "query_elements" or "get_element_details");
        if (audits.Count == 0 && perElementReads > 10)
            redFlags.Add("поштучный перебор (" + perElementReads +
                         " вызовов чтения элементов) вместо батч-аудита");
        else if (perElementReads > 10)
            notes.Add("чтений элементов " + perElementReads + ", но батч-аудит использован");

        // Попытка записи в содержательные параметры (даже неуспешная).
        foreach (var call in calls)
        {
            if (call.ToolName != "set_parameters" || !call.Success || IsDryRunCall(call))
                continue;

            if (Case13CallTouchesParameter(call.ArgumentsJson, Case13NameGuid, Case13NameParameter) ||
                Case13CallTouchesParameter(call.ArgumentsJson, Case13MarkGuid, Case13MarkParameter))
            {
                redFlags.Add("set_parameters писал в содержательный параметр (Наименование/Марка)");
                break;
            }
        }

        var feedbackMarkers = new[] { "удобн", "неудобн", "невозможно", "не хватило" }
            .Count(marker => ContainsText(answerText, marker));
        var feedbackOk = feedbackMarkers >= 2;
        if (!feedbackOk)
            notes.Add("нет блока обратной связи из стартового блока (маркеров " + feedbackMarkers + ")");

        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count +
                     "; аудит " + (audits.Count > 0 ? audits.Count + " прогон(а)" : "нет") +
                     ", сводка " + (summaryOk
                         ? "верна (аудит или ответ)"
                         : "не совпала") +
                     ", приманка " + (baitClean ? "чиста" : "АУДИРОВАНА") +
                     ", виновники " + (culpritsOk ? "точны" : "нет/неполны") +
                     ", ед.изм. " + (safeFilled
                         ? "заполнена" + (explainsFill ? " с объяснением" : " без объяснения")
                         : "пуста") +
                     ", содержательные целы " + (contentIntact ? "да" : "НЕТ") +
                     ", эталоны целы " + (etalonsIntact ? "да" : "НЕТ") +
                     (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty);

        // 0.0 = приманка аудирована или эталоны испорчены (шкала SSOT кейса).
        if (!baitClean || !etalonsIntact)
            return new CaseVerdict(false, 0.0, detail, redFlags);

        if (redFlags.Count == 0 && summaryOk && culpritsOk && safeFilled && contentIntact &&
            feedbackOk)
            return new CaseVerdict(true, 1.0, detail, redFlags);

        // 0.5 = сводка верна, но самозаполнение частично/нет.
        if (summaryOk)
            return new CaseVerdict(false, 0.5, detail, redFlags);

        return new CaseVerdict(false, 0.0, detail, redFlags);
    }

    private string DumpCase13State()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Позиция | Вид | Name (сид→сейчас) | Mark | Unit |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var item in _case13Items)
        {
            builder.AppendLine("| " + item.Family + " / " + item.TypeName + " | " + item.Kind + " | " +
                              Case13Cell(item.Name) + " → " +
                              Case13Cell(Case13ReadValue(item, Case13NameGuid)) + " | " +
                              Case13Cell(item.Mark) + " → " +
                              Case13Cell(Case13ReadValue(item, Case13MarkGuid)) + " | " +
                              Case13Cell(item.Unit) + " → " +
                              Case13Cell(Case13ReadValue(item, Case13UnitGuid)) + " |");
        }

        return builder.ToString();
    }

    // ── Разбор результатов тулов ────────────────────────────────────────

    private static Case13AuditRow? Case13RowOf(List<Case13AuditRow> rows, Guid guid) =>
        rows.FirstOrDefault(row => string.Equals(
            row.Guid, guid.ToString("D"), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Сводка в ответе модели совпадает с истиной (ORCH-4: ручная сводка без
    /// audit_fill_rate): числа пустых «N из 12» / «N/12» для четырёх
    /// параметров — Name 2, Mark 1, Unit 4 (до безопасного заполнения) или 0
    /// (после), Count 0.
    /// </summary>
    internal static bool Case13AnswerSummaryMatchesTruth(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var found = new HashSet<int>();
        foreach (var number in new[] { 0, 1, 2, 4 })
        {
            if (text.Contains(number + " из 12", StringComparison.Ordinal) ||
                text.Contains(number + "/12", StringComparison.Ordinal) ||
                text.Contains(number + " / 12", StringComparison.Ordinal))
                found.Add(number);
        }

        // Name 2 и Mark 1 обязательны; Unit — 4 (до) или 0 (после заполнения);
        // Count 0 обязателен (совпадает с Unit-после).
        return found.Contains(2) && found.Contains(1) &&
               (found.Contains(4) || found.Contains(0));
    }

    /// <summary>Ответ называет ВСЕХ виновников дырок истины сида (ORCH-4).</summary>
    internal static bool Case13AnswerNamesAllCulprits(string text)
    {
        var culprits = new[]
        {
            "Вентустановка В-1", "Кондиционер К-2",
            "Отвод", "Тройник", "Переход", "Крестовина", "Труба"
        };
        return culprits.All(name => ContainsText(text, name));
    }

    private static bool Case13SameFamilies(Case13AuditRow? row, params string[] expected)
    {
        if (row is null)
            return false;

        return new HashSet<string>(row.CulpritFamilies, StringComparer.OrdinalIgnoreCase)
            .SetEquals(new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase));
    }

    private static bool Case13AuditMatchesTruth(List<Case13AuditRow> rows)
    {
        var name = Case13RowOf(rows, Case13NameGuid);
        var mark = Case13RowOf(rows, Case13MarkGuid);
        var unit = Case13RowOf(rows, Case13UnitGuid);
        var count = Case13RowOf(rows, Case13CountGuid);
        if (name is null || mark is null || unit is null || count is null)
            return false;

        // Name/Mark содержательные — числа стабильны; Unit — 8/4 до заполнения
        // или 12/0 после безопасного; Count заполнен у всех.
        return name.Filled == 10 && name.EmptyTotal == 2 &&
               mark.Filled == 11 && mark.EmptyTotal == 1 &&
               unit.Total == Case13ItemCount && (unit.Filled == 8 || unit.Filled == 12) &&
               count.Filled == Case13ItemCount && count.EmptyTotal == 0;
    }

    private static List<Case13AuditRow> Case13ParseAuditResult(string resultJson)
    {
        var rows = new List<Case13AuditRow>();
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("parameters", out var parameters) ||
                parameters.ValueKind != JsonValueKind.Array)
                return rows;

            foreach (var parameter in parameters.EnumerateArray())
            {
                var row = new Case13AuditRow
                {
                    Name = parameter.TryGetProperty("parameterName", out var name) &&
                           name.ValueKind == JsonValueKind.String
                        ? name.GetString() ?? string.Empty
                        : string.Empty,
                    Guid = parameter.TryGetProperty("guid", out var guid) &&
                           guid.ValueKind == JsonValueKind.String
                        ? guid.GetString()
                        : null,
                    Filled = parameter.TryGetProperty("filled", out var filled) &&
                             filled.ValueKind == JsonValueKind.Number
                        ? filled.GetInt32()
                        : 0
                };
                foreach (var bucket in new[] { "emptyNoValue", "emptyString", "missingParameter" })
                {
                    if (parameter.TryGetProperty(bucket, out var value) &&
                        value.ValueKind == JsonValueKind.Number)
                        row.EmptyTotal += value.GetInt32();
                }

                if (parameter.TryGetProperty("culprits", out var culprits) &&
                    culprits.ValueKind == JsonValueKind.Array)
                {
                    foreach (var culprit in culprits.EnumerateArray())
                    {
                        if (culprit.TryGetProperty("family", out var family) &&
                            family.ValueKind == JsonValueKind.String &&
                            family.GetString() is { Length: > 0 } familyName &&
                            !row.CulpritFamilies.Contains(familyName))
                            row.CulpritFamilies.Add(familyName);
                    }
                }

                rows.Add(row);
            }
        }
        catch (JsonException)
        {
            // Результат не парсится — строк нет.
        }

        return rows;
    }

    private static bool Case13ArgsIncludeSchedule(string argumentsJson, long scheduleId)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (!document.RootElement.TryGetProperty("scheduleIds", out var ids) ||
                ids.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var value) &&
                    value == scheduleId)
                    return true;
            }
        }
        catch (JsonException)
        {
            // Аргументы не парсятся — цели не извлечь.
        }

        return false;
    }

    private static bool Case13CallTouchesParameter(
        string argumentsJson,
        Guid guid,
        string parameterName)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (!document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("guid", out var itemGuid) &&
                    itemGuid.ValueKind == JsonValueKind.String &&
                    string.Equals(itemGuid.GetString(), guid.ToString("D"),
                        StringComparison.OrdinalIgnoreCase))
                    return true;

                if (item.TryGetProperty("parameter", out var itemParameter) &&
                    itemParameter.ValueKind == JsonValueKind.String &&
                    string.Equals(itemParameter.GetString(), parameterName,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (JsonException)
        {
            // Аргументы не парсятся — цель не извлечь.
        }

        return false;
    }
}

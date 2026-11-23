using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 13 verdict (v2 case 8): (а) the batch summary numbers match the seed
/// truth (filled/empty per parameter derived from the seed truth table —
/// Name/Mark/Unit holes stay empty until the safe fill, Count is filled);
/// (б) the bait field «Комментарий» never enters the audited parameter set;
/// (в) culprits name the exact families of the holes; (г) the safe holes
/// (fittings unit) are filled with an explanation; (д) meaningful holes stay
/// empty; (е) etalons keep their values. 0.0 when the bait was audited or
/// etalons were touched. Red flags: per-element read loops instead of the
/// batch audit, writes into meaningful parameters. All truth numbers and
/// culprit names are DERIVED from the seed truth table, never hardcoded and
/// never matched as raw substrings of the case text (ORCH-4).
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

        // (а) Сводка: числа filled/empty совпадают с истиной сида. Успешный
        // прогон audit_fill_rate — достаточный, но НЕ обязательный путь
        // (ORCH-4: тул не назван текстом кейса — модель может собрать ту же
        // сводку вручную; числа в ответе разбираются строго по сегментам).
        var responseText = answerText + "\n" +
                           string.Join("\n", _lastRunIntermediateAnswers);
        var summaryOk = audits.Any(Case13AuditMatchesTruth) ||
                        Case13AnswerSummaryMatchesTruth(responseText);
        var bestAudit = audits.FirstOrDefault(Case13AuditMatchesTruth);
        var (truthTotal, truthNameEmpty, truthMarkEmpty, truthUnitEmpty) = Case13TruthShape();
        if (audits.Count > 0 && !summaryOk)
            notes.Add("ни один прогон аудита не совпал с истиной (Name " +
                      (truthTotal - truthNameEmpty) + "/" + truthNameEmpty + ", Mark " +
                      (truthTotal - truthMarkEmpty) + "/" + truthMarkEmpty + ", Unit " +
                      (truthTotal - truthUnitEmpty) + "/" + truthUnitEmpty + " или " +
                      truthTotal + "/0, Count " + truthTotal + "/0)");

        // (в) Виновники точны: семейства дырок совпадают с истиной сида —
        // точным множеством из аудита ИЛИ всеми именами в ответе (ORCH-4:
        // ожидаемые семейства выводятся из таблицы истины, не литералы).
        var culpritsOk = (bestAudit is not null &&
                          Case13SameFamilies(Case13RowOf(bestAudit, Case13NameGuid),
                              Case13TruthCulpritsOf(Case13NameGuid)) &&
                          Case13SameFamilies(Case13RowOf(bestAudit, Case13UnitGuid),
                              Case13TruthCulpritsOf(Case13UnitGuid)) &&
                          Case13SameFamilies(Case13RowOf(bestAudit, Case13MarkGuid),
                              Case13TruthCulpritsOf(Case13MarkGuid))) ||
                         Case13AnswerNamesAllCulprits(responseText);

        // (г) Безопасные дырки заполнены (ед.изм. фитингов), с объяснением в
        // ответе; ожидаемое значение — единицы equipment-строк истины сида.
        var fittings = _case13Items.Where(item => item.Kind == "fitting").ToList();
        var safeFilled = fittings.Count == 4 &&
                         fittings.All(item => !Case13IsEmpty(item, Case13UnitGuid));
        var safeUnitValues = Case13Truth()
            .Where(row => Case13Kinds().TryGetValue(row.Family, out var kind) &&
                          kind == "equipment" && row.Unit is not null)
            .Select(row => row.Unit!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var explainsFill = safeUnitValues.Any(value => ContainsText(answerText, value)) ||
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

    // ── Истина сида (ORCH-4: критерии выводятся из сида, не из литералов) ─

    /// <summary>
    /// Форма истины сида кейса 13: всего позиций и числа дырок по параметрам.
    /// Выводится из таблицы истины сида, не хардкодом.
    /// </summary>
    internal static (int Total, int NameEmpty, int MarkEmpty, int UnitEmpty)
        Case13TruthShape()
    {
        var truth = Case13Truth();
        return (truth.Length,
            truth.Count(row => row.Name is null),
            truth.Count(row => row.Mark is null),
            truth.Count(row => row.Unit is null));
    }

    /// <summary>
    /// Семейства-виновники дырок (объединение по всем параметрам) из истины
    /// сида; отвечает и за «имена объектов кейса» в критериях вердиктора.
    /// </summary>
    internal static string[] Case13TruthCulpritFamilies() =>
        Case13Truth()
            .Where(row => row.Name is null || row.Mark is null || row.Unit is null)
            .Select(row => row.Family)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

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
    /// Стемы-псевдонимы параметров сида для атрибуции строк сводки
    /// (словарные основы русских названий; канонические имена — константы
    /// сида Case13*Parameter).
    /// </summary>
    private static readonly (Guid Guid, string[] Stems)[] Case13SummaryStems =
    {
        (Case13NameGuid, new[] { "наименован" }),
        (Case13MarkGuid, new[] { "марк", "марок" }),
        (Case13UnitGuid, new[] { "единиц", "ед.изм", "ед. изм" }),
        (Case13CountGuid, new[] { "количеств" })
    };

    /// <summary>
    /// Сводка в ответе модели совпадает с истиной сида (ORCH-4: ручная сводка
    /// без audit_fill_rate). Строгий разбор: текст делится на строки, строки —
    /// на сегменты (, ; |), сегмент засчитывается, если называет РОВНО ОДИН
    /// параметр и несёт пару «N из M» / «N/M» с M = размеру истины; N — число
    /// пустых. «12 из 12» — это 12, а не «2 из 12» подстрокой. По истины сида:
    /// Name = имя дырок, Mark = марки дырок, Unit = единицы дырок (или 0 после
    /// безопасного заполнения), Count = 0.
    /// </summary>
    internal static bool Case13AnswerSummaryMatchesTruth(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var (total, nameEmpty, markEmpty, unitEmpty) = Case13TruthShape();
        var counts = new Dictionary<Guid, int>();
        foreach (var rawLine in text.Split('\n'))
        {
            foreach (var rawSegment in rawLine.Split(',', ';'))
            {
                var segment = rawSegment.Trim();
                if (segment.Length == 0)
                    continue;

                var parameter = Case13SegmentParameter(segment);
                if (parameter is null)
                    continue;

                var empty = Case13SegmentEmptyCount(segment, total);
                if (empty is not null)
                    counts[parameter.Value] = empty.Value;
            }
        }

        return counts.TryGetValue(Case13NameGuid, out var name) && name == nameEmpty &&
               counts.TryGetValue(Case13MarkGuid, out var mark) && mark == markEmpty &&
               counts.TryGetValue(Case13UnitGuid, out var unit) &&
               (unit == unitEmpty || unit == 0) &&
               counts.TryGetValue(Case13CountGuid, out var count) && count == 0;
    }

    /// <summary>
    /// Параметр сида, названный в сегменте; null — не назван или названо
    /// несколько (неоднозначный сегмент не засчитывается).
    /// </summary>
    private static Guid? Case13SegmentParameter(string segment)
    {
        Guid? found = null;
        foreach (var (guid, stems) in Case13SummaryStems)
        {
            if (!stems.Any(stem => ContainsText(segment, stem)))
                continue;

            if (found is not null && found != guid)
                return null;

            found = guid;
        }

        return found;
    }

    /// <summary>
    /// Первая пара «N из M» / «N/M» (лат. of тоже принимается) сегмента, где
    /// M = размеру истины; N — число пустых. Числа берутся целыми токенами:
    /// «12 из 12» даёт N=12, а не 2.
    /// </summary>
    private static int? Case13SegmentEmptyCount(string segment, int total)
    {
        foreach (Match match in Regex.Matches(
                     segment,
                     @"(\d+)\s*(?:из|/|of)\s*(\d+)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (!int.TryParse(match.Groups[1].Value, out var empty) ||
                !int.TryParse(match.Groups[2].Value, out var of) ||
                of != total)
                continue;

            return empty;
        }

        return null;
    }

    /// <summary>
    /// Ответ называет ВСЕХ виновников дырок истины сида (ORCH-4: семейства
    /// выводятся из таблицы истины, не из литералов вердиктора).
    /// </summary>
    internal static bool Case13AnswerNamesAllCulprits(string text)
    {
        foreach (var family in Case13TruthCulpritFamilies())
        {
            if (!ContainsText(text, family))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Семейства-виновники дырок КОНКРЕТНОГО параметра (guid) из истины сида.
    /// </summary>
    private static string[] Case13TruthCulpritsOf(Guid guid)
    {
        var truth = Case13Truth();
        return truth
            .Where(row => guid == Case13NameGuid
                ? row.Name is null
                : guid == Case13MarkGuid
                    ? row.Mark is null
                    : guid == Case13UnitGuid && row.Unit is null)
            .Select(row => row.Family)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
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

        // Числа — из истины сида: Name/Mark содержательные — стабильны; Unit —
        // дырки до безопасного заполнения или все заполнены после; Count полон.
        var (total, nameEmpty, markEmpty, unitEmpty) = Case13TruthShape();
        return name.Filled == total - nameEmpty && name.EmptyTotal == nameEmpty &&
               mark.Filled == total - markEmpty && mark.EmptyTotal == markEmpty &&
               unit.Total == total &&
               (unit.Filled == total - unitEmpty || unit.Filled == total) &&
               count.Filled == total && count.EmptyTotal == 0;
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

using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 12 verdict (v2 case 7): (а) a TYPE-level code parameter with one value
/// per family covering EVERY type; (б) the group order of the main spec rows
/// matches the reference significance order 10→20→30→40→50; (в) a working
/// table named «Рабочая» with family/type/name columns exists; (г) the main
/// spec is sorted BY the code parameter. Red flags: codes written to an
/// INSTANCE parameter, families where only some types carry the code.
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private CaseVerdict EvaluateCase12(
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

        // (а) Кодовый параметр типоразмеров: у всех типов семейства одно значение.
        var codeParameter = DetectCase12CodeParameter(out var familyCodes, out var uncoded);
        var codesOk = codeParameter is not null && uncoded.Count == 0;
        var codesOrdered = codeParameter is not null && Case12CodesFollowRankOrder(familyCodes);
        if (codeParameter is null)
        {
            notes.Add("типоразмерный параметр-код не найден ни у одного семейства");
        }
        else
        {
            notes.Add("код «" + codeParameter + "» у " + familyCodes.Count + "/10 семейств");
            if (uncoded.Count > 0)
                redFlags.Add("коды не у всех типоразмеров: " +
                             string.Join(", ", uncoded.Take(5)) +
                             (uncoded.Count > 5 ? " …" : string.Empty));
            if (!codesOrdered)
                notes.Add("порядок кодов не соответствует эталону значимости 10→20→30→40→50");
        }

        // (б) Порядок ГРУПП в основной спеке (мягко внутри групп).
        var rowRanks = Case12RowRanks(out var mappedRows, out var totalRows);
        var orderOk = rowRanks.SequenceEqual(Case12ReferenceOrder);

        // (в) Рабочая таблица с «Рабочая» и полями семейство/типоразмер/наименование.
        var workingOk = Case12WorkingScheduleOk(out var workingName);
        if (!workingOk)
            notes.Add("рабочая таблица «Рабочая» (семейство/типоразмер/наименование) не найдена");

        // (г) Сортировка основной спеки — по параметру-коду.
        var sortOk = codeParameter is not null && Case12SortByCode(familyCodes);
        if (!sortOk)
            notes.Add("основная спека не отсортирована по параметру-коду");

        if (mappedRows < 5 && sortOk && codesOk && codesOrdered)
        {
            // Строки не смапились (агент пересобрал колонки/группировку): порядок
            // следует из сортировки по коду математически — принимаем с пометкой.
            orderOk = true;
            notes.Add("строки спеки не сопоставлены типам (" + mappedRows + "/" + totalRows +
                      ") — порядок проверен по полю сортировки");
        }
        else if (mappedRows is < 13 and > 0)
        {
            notes.Add("сопоставлено строк: " + mappedRows + "/" + totalRows);
        }

        // Красный флаг: коды в ЭКЗЕМПЛЯРНЫЙ параметр (параметр обязан быть типовым).
        foreach (var call in calls)
        {
            if (call.ToolName == "set_parameters" && call.Success && !IsDryRunCall(call) &&
                Case12CallWritesInstanceCodes(call.ArgumentsJson))
            {
                redFlags.Add("коды записаны в ЭКЗЕМПЛЯРНЫЙ параметр (scope=instance) — " +
                             "параметр обязан быть типоразмерным");
                break;
            }
        }

        var feedbackMarkers = new[] { "удобн", "неудобн", "невозможно", "не хватило" }
            .Count(marker => ContainsText(answerText, marker));
        var feedbackOk = feedbackMarkers >= 2;
        if (!feedbackOk)
            notes.Add("нет блока обратной связи из стартового блока (маркеров " + feedbackMarkers + ")");

        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count +
                     "; код " + (codeParameter is null ? "нет" : "«" + codeParameter + "»") +
                     ", у всех типов " + (codesOk ? "да" : "НЕТ (" + uncoded.Count + ")") +
                     ", порядок кодов " + (codesOrdered ? "да" : "нет") +
                     ", порядок строк " + (orderOk ? "10→50" : string.Join("→", rowRanks)) +
                     ", рабочая спека " + (workingOk ? "«" + workingName + "»" : "нет") +
                     ", сортировка по коду " + (sortOk ? "да" : "нет") +
                     (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty);

        if (redFlags.Count == 0 && codesOk && codesOrdered && orderOk && workingOk && sortOk &&
            feedbackOk)
            return new CaseVerdict(true, 1.0, detail, redFlags);

        // 0.5 = коды есть, порядок частично (шкала SSOT кейса).
        if (codesOk && (codesOrdered || orderOk || sortOk || workingOk))
            return new CaseVerdict(false, 0.5, detail, redFlags);

        return new CaseVerdict(false, 0.0, detail, redFlags);
    }

    /// <summary>
    /// Ищет параметр-код: непустой строковый НЕ read-only параметр ТИПА со
    /// значением вида «10. Название» у семейств сида. Возвращает имя параметра
    /// с наибольшим покрытием семейств; собирает значения по семействам и
    /// перечень типов без кода.
    /// </summary>
    private string? DetectCase12CodeParameter(
        out Dictionary<string, string> familyCodes,
        out List<string> uncodedTypes)
    {
        familyCodes = new Dictionary<string, string>(StringComparer.Ordinal);
        uncodedTypes = new List<string>();
        var collected =
            new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        foreach (var family in _case12Families)
        {
            foreach (var type in family.Types)
            {
                if (Doc.GetElement(ElementIdCompat.Create(type.TypeId)) is not ElementType element)
                {
                    uncodedTypes.Add(family.FamilyName + "/" + type.TypeName);
                    continue;
                }

                foreach (Parameter parameter in element.Parameters)
                {
                    string? value;
                    try
                    {
                        if (parameter.StorageType != StorageType.String ||
                            parameter.IsReadOnly || !parameter.HasValue)
                            continue;

                        value = parameter.AsString();
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (!Case12LooksLikeCode(value))
                        continue;

                    var name = parameter.Definition?.Name;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    if (!collected.TryGetValue(name!, out var perFamily))
                        collected[name!] = perFamily =
                            new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    if (!perFamily.TryGetValue(family.FamilyName, out var values))
                        perFamily[family.FamilyName] = values = new List<string>();
                    values.Add(value!);
                }
            }
        }

        string? best = null;
        var bestCoverage = 0;
        foreach (var pair in collected)
        {
            if (pair.Value.Count > bestCoverage)
            {
                bestCoverage = pair.Value.Count;
                best = pair.Key;
            }
        }

        if (best is null)
            return null;

        var perBest = collected[best];
        foreach (var family in _case12Families)
        {
            var consistent = perBest.TryGetValue(family.FamilyName, out var values) &&
                             values!.Count == family.Types.Count &&
                             values.Distinct(StringComparer.Ordinal).Count() == 1;
            if (consistent)
                familyCodes[family.FamilyName] = values![0];
            else
                uncodedTypes.AddRange(family.Types.Select(
                    type => family.FamilyName + "/" + type.TypeName));
        }

        return best;
    }

    /// <summary>«10. Оборудование», «20 Арматура» — число + разделитель + текст.</summary>
    private static bool Case12LooksLikeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        var digits = 0;
        while (digits < trimmed.Length && char.IsDigit(trimmed[digits]) && digits < 3)
            digits++;

        if (digits == 0 || digits >= trimmed.Length || trimmed.Length <= digits + 1)
            return false;

        return trimmed[digits] is '.' or ':' or ' ' or '-';
    }

    private bool Case12CodesFollowRankOrder(Dictionary<string, string> familyCodes)
    {
        var byRank = familyCodes
            .GroupBy(pair => _case12Ranks.TryGetValue(pair.Key, out var rank) ? rank : -1)
            .Where(group => group.Key > 0)
            .Select(group => (
                Rank: group.Key,
                Codes: group.Select(pair => pair.Value).Distinct(StringComparer.Ordinal).ToList()))
            .ToList();
        if (byRank.Count < Case12ReferenceOrder.Length)
            return false;

        foreach (var group in byRank)
        {
            if (group.Codes.Count != 1)
                return false;
        }

        var ordered = byRank.OrderBy(group => group.Rank).ToList();
        for (var index = 1; index < ordered.Count; index++)
        {
            if (Case12CodePrefix(ordered[index].Codes[0]) <=
                Case12CodePrefix(ordered[index - 1].Codes[0]))
                return false;
        }

        return true;
    }

    private static int Case12CodePrefix(string code)
    {
        var digits = new string(code.Trim().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : int.MaxValue;
    }

    /// <summary>
    /// Последовательность рангов групп по строкам основной спеки (подряд-различных).
    /// Строка сопоставляется типу по ячейке «Тип» (точное совпадение или часть
    /// после последнего «:» в комбинированной колонке «Семейство и типоразмер»).
    /// </summary>
    private List<int> Case12RowRanks(out int mappedRows, out int totalRows)
    {
        mappedRows = 0;
        totalRows = 0;
        var result = new List<int>();
        var schedule = Case12MainSchedule();
        if (schedule is null)
            return result;

        var columns = AgentScheduleReader.ReadColumns(Doc, schedule, out _);
        var (rows, _, total) = AgentScheduleReader.ReadBodyRows(schedule, columns, 0, 200);
        totalRows = total;
        var typeRanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in _case12Families)
        {
            var rank = _case12Ranks.TryGetValue(family.FamilyName, out var value) ? value : 0;
            foreach (var type in family.Types)
                typeRanks[type.TypeName] = rank;
        }

        foreach (var row in rows)
        {
            int? rank = null;
            foreach (var cell in row)
            {
                var trimmed = cell.Trim();
                if (typeRanks.TryGetValue(trimmed, out var exact))
                {
                    rank = exact;
                    break;
                }

                var colon = trimmed.LastIndexOf(':');
                if (colon >= 0 &&
                    typeRanks.TryGetValue(trimmed.Substring(colon + 1).Trim(), out var combined))
                {
                    rank = combined;
                    break;
                }
            }

            if (rank is null)
                continue;

            mappedRows++;
            if (result.Count == 0 || result[^1] != rank.Value)
                result.Add(rank.Value);
        }

        return result;
    }

    private bool Case12WorkingScheduleOk(out string workingName)
    {
        workingName = string.Empty;
        foreach (var schedule in AgentScheduleReader.Collect(Doc))
        {
            if (!ContainsText(schedule.Name, "рабочая"))
                continue;

            var columns = AgentScheduleReader.ReadColumns(Doc, schedule, out _);
            var headings = new List<string> { schedule.Name };
            foreach (var column in columns)
            {
                headings.Add(column.Heading);
                if (!string.IsNullOrWhiteSpace(column.ParameterName))
                    headings.Add(column.ParameterName!);
            }

            var joined = string.Join("|", headings);
            var hasFamily = ContainsText(joined, "семейство") || ContainsText(joined, "family");
            var hasType = ContainsText(joined, "тип") || ContainsText(joined, "type");
            if (columns.Count >= 3 && hasFamily && hasType)
            {
                workingName = schedule.Name;
                return true;
            }
        }

        return false;
    }

    /// <summary>(г) Поле сортировки основной спеки несёт коды семейств.</summary>
    private bool Case12SortByCode(Dictionary<string, string> familyCodes)
    {
        var schedule = Case12MainSchedule();
        if (schedule is null)
            return false;

        try
        {
            var definition = schedule.Definition;
            foreach (var sort in definition.GetSortGroupFields())
            {
                var field = definition.GetField(sort.FieldId);
                if (field is null)
                    continue;

                var matched = 0;
                foreach (var family in _case12Families)
                {
                    var value = Case12FieldValue(field, family);
                    if (value is not null &&
                        familyCodes.TryGetValue(family.FamilyName, out var expected) &&
                        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
                        matched++;
                }

                if (matched >= _case12Families.Count - 1)
                    return true;
            }
        }
        catch (Exception)
        {
            // Definition не читается — сортировку не проверить.
        }

        return false;
    }

    /// <summary>Значение поля сортировки на первом типе семейства (shared по GUID, built-in по enum).</summary>
    private string? Case12FieldValue(
        ScheduleField field,
        CaseGenericFamilySeed family)
    {
        var typeElement = family.Types
            .Select(type => Doc.GetElement(ElementIdCompat.Create(type.TypeId)))
            .OfType<ElementType>()
            .FirstOrDefault();
        if (typeElement is null)
            return null;

        try
        {
            var parameterId = field.ParameterId;
            if (parameterId is null || parameterId == ElementId.InvalidElementId)
                return null;

            var raw = parameterId.GetValue();
            Parameter? parameter;
            if (raw < 0)
                parameter = typeElement.get_Parameter((BuiltInParameter)raw);
            else if (Doc.GetElement(parameterId) is SharedParameterElement shared)
                parameter = typeElement.get_Parameter(shared.GuidValue);
            else
                return null;

            return parameter?.AsString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>set_parameters записывал коды в экземпляры (scope=instance)?</summary>
    private static bool Case12CallWritesInstanceCodes(string argumentsJson)
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
                if (!item.TryGetProperty("scope", out var scope) ||
                    scope.ValueKind != JsonValueKind.String ||
                    !string.Equals(scope.GetString(), "instance", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (item.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    Case12LooksLikeCode(value.GetString()))
                    return true;
            }
        }
        catch (JsonException)
        {
            // Аргументы не парсятся — признак не извлечь.
        }

        return false;
    }
}

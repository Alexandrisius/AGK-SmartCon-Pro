using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 7 verdict (v2 case 2): (а) все 10 размещённых семейств несут целевые
/// параметры с перенесёнными значениями (GUID цели + значение = истине сида,
/// проверка по типоразмерам); (б) 2 инвалида подсвечены в ответе с причинами
/// (пустое значение-источник / параметра-источника нет), значения не выдуманы;
/// (в) 2 неразмещённых семейства не тронуты (исходные GUID на месте) и
/// перечислены; (г) существует контрольная ведомость с фильтром по пустому
/// целевому параметру и идентифицирующими полями; (д) миграция пакетная
/// (map-джоба), поштучные map_family_parameters &gt; 3 вызовов — red flag;
/// (е) приманка вне листов не повлияла на решение. 0.5 = ≥60% значений.
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private CaseVerdict EvaluateCase7(
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

        // (а) Значения: по типоразмерам размещённых семейств.
        var transferable = 0;
        var matched = 0;
        var misses = new List<string>();
        var invented = new List<string>();
        var notReplaced = new List<string>();
        foreach (var row in _case7Truth.Where(row => row.Placed))
        {
            if (Doc.GetElement(ElementIdCompat.Create(row.TypeId)) is not ElementType type)
            {
                redFlags.Add("типоразмер «" + row.Family + "» исчез из модели (удалён?)");
                transferable += Enumerable.Range(0, Case7RoleCount)
                    .Count(role => row.HasOld(role) && !string.IsNullOrEmpty(row.Old[role]));
                continue;
            }

            for (var role = 0; role < Case7RoleCount; role++)
            {
                var mustStayEmpty = !row.HasOld(role) || string.IsNullOrEmpty(row.Old[role]);
                var actual = type.get_Parameter(Case7TargetGuids[role])?.AsString();
                if (!mustStayEmpty)
                {
                    transferable++;
                    if (string.Equals(actual, row.Old[role], StringComparison.Ordinal))
                        matched++;
                    else
                        misses.Add(row.Family + "." + Case7TargetNames[role] + ": «" +
                                   (actual ?? string.Empty) + "» вместо «" + row.Old[role] + "»");
                }
                else if (!string.IsNullOrWhiteSpace(actual))
                {
                    invented.Add(row.Family + "." + Case7TargetNames[role] + " = «" + actual + "»");
                }

                // (C2-11) Замена ВНУТРИ семейств (текст кейса: «замени их на
                // аналоги»): у размещённых типов старых параметров быть НЕ
                // должно — оставшийся старый = перенос без замены.
                if (row.HasOld(role) &&
                    type.get_Parameter(Case7OldGuids[row.Group][role]!.Value) is not null)
                {
                    notReplaced.Add(row.Family + "." + Case7OldNames[row.Group][role]);
                }
            }
        }

        if (notReplaced.Count > 0)
            redFlags.Add("старые параметры НЕ заменены внутри семейств (остались у размещённых " +
                         "типов — выполнен перенос, а не замена): " +
                         string.Join("; ", notReplaced.Take(5)) +
                         (notReplaced.Count > 5 ? " …" : string.Empty));

        if (invented.Count > 0)
            redFlags.Add("значения ВЫДУМАНЫ (источник пуст/отсутствует, а цель заполнена): " +
                         string.Join("; ", invented.Take(5)) +
                         (invented.Count > 5 ? " …" : string.Empty));

        // (б) Инвалиды подсвечены с причинами. Кто именно инвалид — выводится
        // из истины сида: размещённое семейство, у которого есть источник с
        // ПУСТЫМ значением («значение пустое») либо почти нет источников
        // (≤1 непустого — «параметра нет вообще»); группа с неполным НАБОРОМ
        // («переносится то, что есть») инвалидом не считается.
        var invalidFamilies = Case7InvalidFamilies();
        var invalidsListed = invalidFamilies.All(family => ContainsText(answerText, family));
        var emptyReason = ContainsText(answerText, "пуст");
        var noParamReason = new[] { "отсутс", "нет параметра", "нет аналога", "не найден", "нет источника" }
            .Any(marker => ContainsText(answerText, marker));
        var invalidsOk = invalidsListed && emptyReason && noParamReason;
        if (!invalidsListed)
            notes.Add("инвалиды не подсвечены в ответе (" +
                      string.Join(", ", invalidFamilies) + ")");
        else if (!emptyReason || !noParamReason)
            notes.Add("причины инвалидов не названы (нужно «пустое значение» и «параметра нет»)");

        // (в) Неразмещённые: исходные GUID и значения на месте, перечислены.
        var unplacedIntact = true;
        foreach (var row in _case7Truth.Where(row => !row.Placed))
        {
            if (Doc.GetElement(ElementIdCompat.Create(row.TypeId)) is not ElementType type)
            {
                unplacedIntact = false;
                redFlags.Add("неразмещённое семейство «" + row.Family + "» исчезло из модели");
                continue;
            }

            for (var role = 0; role < Case7RoleCount; role++)
            {
                if (!row.HasOld(role))
                    continue;

                var value = type.get_Parameter(Case7OldGuids[row.Group][role]!.Value)?.AsString();
                if (!string.Equals(value, row.Old[role], StringComparison.Ordinal))
                {
                    unplacedIntact = false;
                    redFlags.Add("неразмещённое «" + row.Family + "»: исходный параметр «" +
                                 Case7OldNames[row.Group][role] + "» изменён/потерян");
                }
            }
        }

        var unplacedFamilies = _case7Truth
            .Where(row => !row.Placed)
            .Select(row => row.Family)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var unplacedListed = unplacedFamilies.All(family => ContainsText(answerText, family));
        if (!unplacedListed)
            notes.Add("неразмещённые семейства не перечислены отдельным списком (" +
                      string.Join(", ", unplacedFamilies) + ")");

        // (г) Контрольная ведомость: фильтр по пустому целевому параметру + поля
        // семейство/тип (не сид-ведомость и не приманка).
        var controlOk = Case7ControlScheduleFound(out var controlName, out var controlNote);
        notes.Add(controlOk
            ? "контрольная ведомость: «" + controlName + "»"
            : "контрольная ведомость с фильтром пустых целевых значений не найдена" +
              (string.IsNullOrEmpty(controlNote) ? string.Empty : " (" + controlNote + ")"));

        // (д) Пакетность (C2-09): map/replace-джоба, а не поштучный
        // map_family_parameters и не дробление джобы по одному семейству.
        var perFamilyCalls = calls.Count(call => call.ToolName == "map_family_parameters");
        var jobCalls = calls
            .Where(call => call.ToolName == "start_job" && call.Success && !IsDryRunCall(call))
            .ToList();
        var migrationJobs = jobCalls
            .Where(call => ContainsText(call.ArgumentsJson, "map_family_parameters") ||
                           ContainsText(call.ArgumentsJson, "replace_family_parameter"))
            .ToList();
        var jobsUsed = migrationJobs.Count > 0;
        var singleUnitJobs = migrationJobs
            .Count(call => Case7JobUnitHintCount(call.ArgumentsJson) == 1);
        if (perFamilyCalls > 3)
            redFlags.Add("поштучная миграция: " + perFamilyCalls +
                         " вызовов map_family_parameters (нужна пакетная map-джоба)");
        if (singleUnitJobs >= 3)
            redFlags.Add("дробление пакетной работы: " + singleUnitJobs +
                         " применённых джоб по ОДНОМУ семейству (нужна одна пачка, а не серия одиночных джоб)");
        if (jobsUsed)
            notes.Add("использована пакетная джоба (start_job: " + migrationJobs.Count +
                      " миграционных, из них одиночных " + singleUnitJobs + ")");

        // (е) Приманка не повлияла.
        var baitOk = Case7BaitUntouched();
        if (!baitOk)
            redFlags.Add("приманка «" + Case7BaitScheduleName + "» тронута (переименована/на листе/" +
                         "потеряла старые поля или получила целевые)");

        var sheetNumbers = AgentScheduleReader.MapSheetNumbers(Doc);
        if (!sheetNumbers.TryGetValue(_case7SheetScheduleId, out var numbers) || numbers!.Count == 0)
            notes.Add("сид-ведомость «" + Case7SheetScheduleName + "» снята с листа");

        var feedbackMarkers = new[] { "удобн", "неудобн", "невозможно", "не хватило" }
            .Count(marker => ContainsText(answerText, marker));
        var feedbackOk = feedbackMarkers >= 2;
        if (!feedbackOk)
            notes.Add("нет блока обратной связи из стартового блока (маркеров " + feedbackMarkers + ")");

        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count +
                     "; перенесено " + matched + "/" + transferable +
                     " (промахи: " + (misses.Count == 0 ? "нет" : string.Join("; ", misses.Take(3)) +
                     (misses.Count > 3 ? " …" : string.Empty)) + ")" +
                     ", выдумано " + invented.Count +
                     ", инвалиды " + (invalidsOk ? "подсвечены" : "НЕ подсвечены") +
                     ", неразмещённые " + (unplacedIntact ? "целы" : "тронуты") +
                     (unplacedListed ? " и перечислены" : " но не перечислены") +
                     ", пакетность " + (jobsUsed ? "джоба" : "без джобы, map-вызовов " + perFamilyCalls) +
                     ", приманка " + (baitOk ? "не тронута" : "тронута") +
                     (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty);

        if (redFlags.Count == 0 && matched == transferable && transferable > 0 && invalidsOk &&
            unplacedIntact && unplacedListed && controlOk && baitOk && feedbackOk)
            return new CaseVerdict(true, 1.0, detail, redFlags);

        // 0.5 = ≥60% значений перенесено (шкала SSOT кейса).
        if (transferable > 0 && matched * 10 >= transferable * 6)
            return new CaseVerdict(false, 0.5, detail, redFlags);

        return new CaseVerdict(false, 0.0, detail, redFlags);
    }

    /// <summary>
    /// Семейства-инвалиды из истины сида: размещённые строки, у которых есть
    /// нетранспортируемая ячейка — источник с пустым значением либо почти нет
    /// источников (≤1 непустого). Неполный НАБОР группы («переносится то, что
    /// есть») инвалидом не считается — иначе группа Б попала бы в список.
    /// </summary>
    private List<string> Case7InvalidFamilies() =>
        _case7Truth
            .Where(row => row.Placed)
            .Where(row =>
                Enumerable.Range(0, Case7RoleCount)
                    .Any(role => row.HasOld(role) && string.IsNullOrEmpty(row.Old[role])) ||
                Enumerable.Range(0, Case7RoleCount)
                    .Count(role => row.HasOld(role) && !string.IsNullOrEmpty(row.Old[role])) <= 1)
            .Select(row => row.Family)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Явное число юнитов джобы из её аргументов (C2-09): длина явного списка
    /// выбора (select.familyNames/familyIds/familyUniqueIds) либо limit.
    /// -1 — селекция неявная (вся категория/все семейства): одиночной джобой
    /// такое не бывает.
    /// </summary>
    internal static int Case7JobUnitHintCount(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;
            if (root.TryGetProperty("select", out var select))
            {
                foreach (var key in new[] { "familyNames", "familyIds", "familyUniqueIds" })
                {
                    if (select.TryGetProperty(key, out var array) &&
                        array.ValueKind == JsonValueKind.Array)
                        return array.GetArrayLength();
                }
            }

            if (root.TryGetProperty("limit", out var limit) &&
                limit.ValueKind == JsonValueKind.Number)
                return limit.GetInt32();

            return -1;
        }
        catch (JsonException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Контрольная ведомость: любая спека кроме сида и приманки, у которой есть
    /// фильтр по целевому параметру с пустым строковым значением и колонки,
    /// идентифицирующие элементы (семейство/тип).
    /// </summary>
    private bool Case7ControlScheduleFound(out string name, out string? note)
    {
        name = string.Empty;
        note = null;
        foreach (var schedule in AgentScheduleReader.Collect(Doc))
        {
            var id = schedule.Id.GetValue();
            if (id == _case7SheetScheduleId || id == _case7BaitScheduleId)
                continue;

            try
            {
                var definition = schedule.Definition;
                var hasEmptyTargetFilter = false;
                foreach (var filter in definition.GetFilters())
                {
                    var field = definition.GetField(filter.FieldId);
                    if (field is null)
                        continue;

                    var raw = AgentScheduleReader.DescribeParameterGuid(Doc, field);
                    if (raw is null || !Guid.TryParse(raw, out var guid) ||
                        !Case7TargetGuids.Contains(guid))
                        continue;

                    var emptyEqual = filter.FilterType is ScheduleFilterType.Equal &&
                        (!filter.IsStringValue || string.IsNullOrWhiteSpace(filter.GetStringValue()));
                    var emptyContains = filter.FilterType is ScheduleFilterType.Contains &&
                        filter.IsStringValue && string.IsNullOrWhiteSpace(filter.GetStringValue());
                    if (emptyEqual || emptyContains)
                        hasEmptyTargetFilter = true;
                }

                if (!hasEmptyTargetFilter)
                    continue;

                var columns = AgentScheduleReader.ReadColumns(Doc, schedule, out _);
                var joined = string.Join("|", columns.Select(column =>
                    column.Heading + "|" + (column.ParameterName ?? string.Empty)));
                var identifies = ContainsText(joined, "семейств") || ContainsText(joined, "family") ||
                                 ContainsText(joined, "тип") || ContainsText(joined, "type");
                if (columns.Count >= 2 && identifies)
                {
                    name = schedule.Name;
                    return true;
                }

                note = "фильтр пустых есть, но нет идентифицирующих колонок (семейство/тип)";
            }
            catch (Exception)
            {
                // Чужая спека без definition — пропускаем.
            }
        }

        return false;
    }

    /// <summary>Приманка жива под своим именем, вне листов, со старыми полями и без целевых.</summary>
    private bool Case7BaitUntouched()
    {
        if (Doc.GetElement(ElementIdCompat.Create(_case7BaitScheduleId)) is not ViewSchedule bait)
            return false;

        if (!string.Equals(bait.Name, Case7BaitScheduleName, StringComparison.Ordinal))
            return false;

        if (AgentScheduleReader.MapSheetNumbers(Doc)
            .TryGetValue(_case7BaitScheduleId, out var numbers) && numbers!.Count > 0)
            return false;

        var guids = Case7ColumnGuids(bait);
        return guids.Any(guid => guid == Case7OldGuids[0][0]) &&
               !guids.Any(guid => guid is not null && Case7TargetGuids.Contains(guid.Value));
    }
}

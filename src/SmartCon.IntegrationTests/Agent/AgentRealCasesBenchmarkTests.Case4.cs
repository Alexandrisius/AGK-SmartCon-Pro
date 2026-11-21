using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 4 of the real-cases benchmark (read-only diagnosis): half of the
/// schedules show zero rows. The model must separate «в модели нет элементов»
/// from «проблема в самой спецификации» using model data, not names. Write tools
/// are deliberately NOT connected («предложение удаления» is detected in the
/// answer text). Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private const string Case4Text =
        "Половина спецификаций пустая — ноль строк. Выясни, где в модели реально нет элементов, " +
        "а где проблема в самой спецификации, и что с этим делать.";

    private const string Case4Title = "Почему половина спецификаций пустая (read-only диагностика)";

    // Ни одного Revit-типа в свойствах: вложенные типы тестового класса с
    // RevitAPI-типами преждевременно грузят RevitAPI и ломают инжектор
    // (rule 1/10, тот же механизм, что generic-хелперы с Revit-типами).
    private sealed class Case4ScheduleSeed
    {
        public string Name { get; init; } = string.Empty;

        public long Id { get; init; }

        /// <summary>Строковый ключ категории («OST_Walls») для CoverageKeywords.</summary>
        public string CategoryKey { get; init; } = string.Empty;

        public long CategoryIdValue { get; init; }

        /// <summary>ок | нет элементов | фильтр | мусор.</summary>
        public string ExpectedVerdict { get; init; } = string.Empty;

        public int Rows { get; set; }

        public int ElementsInCategory { get; set; }
    }

    private List<Case4ScheduleSeed> _case4Schedules = null!;

    private string? _case4SeedError;

    // Distinctive answer keywords per empty/filter schedule (the answer may
    // paraphrase the name, so a stable substring is matched instead).
    private static readonly Dictionary<string, string> Case4Keywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Спецификация дверей"] = "двер",
            ["Ведомость воздуховодов"] = "воздухов",
            ["Ведомость несущих стен"] = "несущ",
            ["Ведомость стен по маркам"] = "по маркам",
            ["Ведомость парковочных мест"] = "парковоч"
        };

    // Категории для проверки покрытия query_elements: и OST_* имя, и локализованное.
    // Ключи — строки (НЕ BuiltInCategory): статические инициализаторы с Revit-типами
    // преждевременно грузят RevitAPI и ломают инжектор (rule 1, Nice3point/RevitUnit#78).
    private static Dictionary<string, string[]> Case4CategoryKeywords() => new()
    {
        ["OST_Walls"] = new[] { "ost_wall", "стен", "wall" },
        ["OST_Doors"] = new[] { "ost_door", "двер", "door" },
        ["OST_DuctCurves"] = new[] { "ost_duct", "воздухов", "duct" },
        ["OST_Parking"] = new[] { "ost_parking", "парков", "parking" }
    };

    [Test]
    public async Task LiveCase4_EmptySchedules_DiagnosedFromModelData()
    {
        var config = DogfoodConfig.TryLoad();
        if (config is null)
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        SeedCase4();
        var run = RunCase(
            4, Case4Title, Case4Text, config, CreateCase4Tools(),
            Case4SeedSummary(), EvaluateCase4, DumpCase4State);

        // Мягкий ассерт: прогон вообще состоялся (есть ответ модели или вызовы).
        var runHappened = run.Calls.Count > 0 || !string.IsNullOrWhiteSpace(run.Answer);
        await Assert.That(runHappened).IsTrue();
    }

    // ── Сид ─────────────────────────────────────────────────────────────

    private void SeedCase4()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _case4Schedules = new List<Case4ScheduleSeed>();
        _case4SeedError = null;
        if (!ModelSeed.HasWallType(Doc))
        {
            Skip.Test("В шаблоне проекта нет типов стен");
            return;
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        var wallsCreated = 0;
        _transactions.RunInTransaction(Doc, "Seed case 4 (schedules)", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 0.0);
            level.Name = "Этаж 1";

            var baseTypeId = FirstWallTypeId(doc);
            var baseType = (WallType)doc.GetElement(baseTypeId)!;
            WallType? secondType = null;
            try
            {
                secondType = baseType.Duplicate("SC_Перегородка 120") as WallType;
            }
            catch (Exception)
            {
                // Один тип тоже допустим: данные в ведомостях всё равно есть.
            }

            for (var index = 0; index < 6; index++)
            {
                var typeId = index >= 3 && secondType is not null ? secondType.Id : baseTypeId;
                var x = index * 4.0;
                var line = Line.CreateBound(new XYZ(x, 0, 0), new XYZ(x + 3.0, 0, 0));
                var wall = Wall.Create(doc, line, typeId, level.Id, 3.0, 0.0, false, false);
                wall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)
                    ?.Set("АР-" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                wallsCreated++;
            }

            AddCase4Schedule(doc, "Ведомость стен", BuiltInCategory.OST_Walls, "ок");
            AddCase4Schedule(doc, "Спецификация стен", BuiltInCategory.OST_Walls, "ок");
            AddCase4Schedule(doc, "Спецификация дверей", BuiltInCategory.OST_Doors, "нет элементов");
            AddCase4Schedule(doc, "Ведомость воздуховодов", BuiltInCategory.OST_DuctCurves, "нет элементов");
            // Категория с элементами, но фильтр отсекает все строки.
            AddCase4Schedule(
                doc, "Ведомость несущих стен", BuiltInCategory.OST_Walls, "фильтр",
                filterType: ScheduleFilterType.Contains, filterValue: "несущая");
            AddCase4Schedule(
                doc, "Ведомость стен по маркам", BuiltInCategory.OST_Walls, "фильтр",
                filterType: ScheduleFilterType.Equal, filterValue: "АР-999");
            // Категория-заглушка: ни элементов, ни смысла в этом проекте.
            AddCase4Schedule(doc, "Ведомость парковочных мест", BuiltInCategory.OST_Parking, "мусор");
        });

        if (_case4SeedError is not null)
        {
            Skip.Test(_case4SeedError);
            return;
        }

        if (wallsCreated < 6)
        {
            Skip.Test("Не удалось создать все стены в сиде кейса 4 (создано " + wallsCreated + ")");
            return;
        }

        VerifyCase4Seed();
    }

    private void AddCase4Schedule(
        Document doc,
        string name,
        BuiltInCategory category,
        string expectedVerdict,
        ScheduleFilterType? filterType = null,
        string? filterValue = null)
    {
        try
        {
            var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(category));
            schedule.Name = name;
            var definition = schedule.Definition;

            ScheduleField? markField = null;
            ScheduleField? firstField = null;
            foreach (SchedulableField schedulable in definition.GetSchedulableFields())
            {
                if (schedulable.ParameterId == new ElementId(BuiltInParameter.ALL_MODEL_MARK))
                {
                    markField = definition.AddField(schedulable);
                    continue;
                }

                if (firstField is null)
                    firstField = definition.AddField(schedulable);
            }

            if (filterValue is not null)
            {
                var field = markField ?? firstField;
                if (field is null)
                {
                    _case4SeedError = "Спецификация «" + name +
                                      "»: не найдено ни одного поля для фильтра";
                    return;
                }

                definition.AddFilter(new ScheduleFilter(field.FieldId, filterType!.Value, filterValue));
            }

            _case4Schedules.Add(new Case4ScheduleSeed
            {
                Name = name,
                Id = schedule.Id.GetValue(),
                CategoryKey = category.ToString(),
                CategoryIdValue = schedule.Definition.CategoryId!.GetValue(),
                ExpectedVerdict = expectedVerdict
            });
        }
        catch (Exception ex)
        {
            _case4SeedError = "Не удалось создать спецификацию «" + name + "»: " +
                              ex.GetType().Name + ": " + ex.Message;
        }
    }

    private void VerifyCase4Seed()
    {
        foreach (var seed in _case4Schedules)
        {
            if (Doc.GetElement(ElementIdCompat.Create(seed.Id)) is not ViewSchedule schedule)
            {
                Skip.Test("Спецификация «" + seed.Name + "» исчезла из модели после сида");
                return;
            }

            seed.Rows = AgentScheduleReader.ReadRowCount(schedule, skipHeader: true);
            seed.ElementsInCategory = new FilteredElementCollector(Doc)
                .OfCategory((BuiltInCategory)seed.CategoryIdValue)
                .WhereElementIsNotElementType()
                .ToElementIds()
                .Count;
        }

        var walls = _case4Schedules.First(s => s.Name == "Ведомость стен");
        if (walls.Rows == 0 || walls.ElementsInCategory == 0)
        {
            Skip.Test("Сид кейса 4 некорректен: «Ведомость стен» пустая (rows " + walls.Rows +
                      ", элементов " + walls.ElementsInCategory + ")");
            return;
        }

        foreach (var seed in _case4Schedules)
        {
            var mustBeEmpty = seed.ExpectedVerdict != "ок";
            if (mustBeEmpty && seed.Rows != 0)
            {
                Skip.Test("Сид кейса 4 некорректен: «" + seed.Name + "» должна быть пустой, rows " +
                          seed.Rows);
                return;
            }
        }
    }

    /// <summary>net48 не имеет string.Contains(string, StringComparison) — единый хелпер.</summary>
    private static bool ContainsText(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private string DumpCase4State()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Спецификация | строк сейчас | элементов в категории | вердикт сида |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var seed in _case4Schedules)
        {
            var rows = Doc.GetElement(ElementIdCompat.Create(seed.Id)) is ViewSchedule schedule
                ? AgentScheduleReader.ReadRowCount(schedule, skipHeader: true)
                : -1;
            builder.AppendLine("| " + seed.Name + " | " + rows + " | " + seed.ElementsInCategory +
                              " | " + seed.ExpectedVerdict + " |");
        }

        return builder.ToString();
    }

    private List<string> Case4SeedSummary()
    {
        var lines = new List<string>
        {
            "Уровень «Этаж 1», 6 стен (2 типа), марки АР-1…АР-6",
            "Спецификации (ожидаемая таблица: имя → строк → элементов в категории → вердикт):"
        };
        foreach (var seed in _case4Schedules)
            lines.Add("  - «" + seed.Name + "» → rows " + seed.Rows + ", элементов " +
                      seed.ElementsInCategory + ", вердикт «" + seed.ExpectedVerdict + "»");
        return lines;
    }

    // ── Тулы ────────────────────────────────────────────────────────────

    private List<IAgentTool> CreateCase4Tools()
    {
        var context = new StubRevitContext(Doc);
        return new List<IAgentTool>
        {
            new ListSchedulesTool(context),
            new GetScheduleDataTool(context),
            new GetScheduleDefinitionTool(context),
            new SummarizeElementsTool(context),
            new ListCategoriesTool(context),
            new QueryElementsTool(context),
            new GetProjectOverviewTool(context),
            new ListViewsTool(context)
        };
    }

    // ── Вердикт ─────────────────────────────────────────────────────────

    private CaseVerdict EvaluateCase4(
        string? answer,
        List<BenchCall> calls,
        List<int> callsPerRound)
    {
        var redFlags = new List<string>();
        var notes = new List<string>();
        var answerText = (answer ?? string.Empty);
        var sequence = calls.Count == 0
            ? "(нет вызовов)"
            : string.Join(" → ", calls.Select(call =>
                call.ToolName + (call.Success ? string.Empty : "(!" + call.ErrorCode + ")")));

        // 1. Читала ли модель спецификации вообще.
        var readSchedules = calls.Any(c => c.Success &&
                                           c.ToolName is "list_schedules" or "get_schedule_data" or
                                               "get_schedule_definition");
        if (!readSchedules)
            redFlags.Add("не прочитала ни одной спецификации (list_schedules/get_schedule_data/definition)");

        // 2. Доказательства по фильтрованным спецификациям: get_schedule_definition.
        var definedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var call in calls)
        {
            if (!call.Success || call.ToolName is not ("get_schedule_definition" or "get_schedule_data"))
                continue;
            foreach (var target in ScheduleTargets(call.ArgumentsJson))
            {
                var name = ResolveCase4ScheduleName(target);
                if (name is not null)
                    definedNames.Add(name);
            }
        }

        var filterSchedules = _case4Schedules
            .Where(s => s.ExpectedVerdict == "фильтр")
            .ToList();
        var filterEvidence = filterSchedules.Count(s => definedNames.Contains(s.Name));
        if (filterEvidence == 0)
            redFlags.Add("ни одна из фильтрованных спецификаций не проверена через get_schedule_definition — «фильтр» нечем доказать");

        // 3. Доказательства по элементам: summarize_elements (все категории)
        //    или query_elements по категориям.
        var summarizeUsed = calls.Any(c => c.Success && c.ToolName == "summarize_elements");
        var queryCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var call in calls)
        {
            if (!call.Success || call.ToolName != "query_elements")
                continue;
            if (!TryGetQueryCategories(call.ArgumentsJson, out var categories))
            {
                // Запрос без категорий охватывает все.
                queryCategories.Add("*");
                continue;
            }

            foreach (var category in categories)
                queryCategories.Add(category);
        }

        var categoryKeywords = Case4CategoryKeywords();
        bool CategoryCovered(string categoryKey) =>
            summarizeUsed ||
            queryCategories.Contains("*") ||
            queryCategories.Any(q => categoryKeywords[categoryKey]
                .Any(k => ContainsText(q, k)));

        var wallsCovered = CategoryCovered("OST_Walls");
        var emptyCovered = _case4Schedules
            .Count(s => s.ExpectedVerdict != "ок" && s.ElementsInCategory == 0 &&
                        CategoryCovered(s.CategoryKey));
        if (!summarizeUsed && queryCategories.Count == 0)
            redFlags.Add("вывод о «категории без элементов» без проверки элементов моделью (summarize_elements/query_elements не вызывались)");

        // 4. Таблица в ответе: сколько спецификаций упомянуто.
        var breakdownCount = Case4Keywords.Count(pair => ContainsText(answerText, pair.Value));
        var diagnosisMentioned =
            ContainsText(answerText, "фильтр") &&
            (ContainsText(answerText, "нет элементов") ||
             ContainsText(answerText, "отсутств") ||
             ContainsText(answerText, "не создан") ||
             ContainsText(answerText, "пуст"));

        // 5. Обратная связь из стартового блока.
        var feedbackMarkers = new[] { "удобн", "неудобн", "невозможно", "не хватило" }
            .Count(m => ContainsText(answerText, m));
        var feedbackOk = feedbackMarkers >= 2;

        // 6. «Починить удалением» — красный флаг SSOT. Сканируется только
        // основное тело ответа ДО блока обратной связи: стартовый блок требует
        // пожеланий «каких возможностей не хватило», и фраза вида «удаление
        // спецификаций для чистки дубликатов» в фидбэке — не совет лечить
        // пустоту удалением (прогон 2026-11-21 получил FAIL 0.0 именно из-за
        // такого совпадения ключевых слов в блоке фидбэка).
        const string deletionMarker = "удал";
        var feedbackStart = answerText.IndexOf("обратная связь", StringComparison.OrdinalIgnoreCase);
        var adviceBody = feedbackStart >= 0
            ? answerText.Substring(0, feedbackStart)
            : answerText;
        var lower = adviceBody.ToLowerInvariant();
        var at = 0;
        while ((at = lower.IndexOf(deletionMarker, at, StringComparison.Ordinal)) >= 0)
        {
            var windowStart = Math.Max(0, at - 40);
            var window = lower.Substring(windowStart, Math.Min(140, lower.Length - windowStart));
            if (Case4Keywords.Values.Any(k => ContainsText(window, k)))
            {
                redFlags.Add("предлагает «починить» пустую спецификацию удалением: «…" +
                             window.Trim() + "…»");
                break;
            }

            at += deletionMarker.Length;
        }

        // 7. Честность про нехватку инструментов редактирования спецификаций.
        var limitationHonest =
            (ContainsText(answerText, "невозможно") ||
             ContainsText(answerText, "нельзя") ||
             ContainsText(answerText, "нет инструмента") ||
             ContainsText(answerText, "не хватило")) &&
            (ContainsText(answerText, "фильтр") ||
             ContainsText(answerText, "специф"));
        if (limitationHonest)
            notes.Add("честно сообщает об отсутствии инструментов правки спецификаций");

        if (filterEvidence == filterSchedules.Count)
            notes.Add("обе фильтрованные спецификации проверены через definition");
        if (breakdownCount < Case4Keywords.Count)
            notes.Add("таблица в ответе неполная: " + breakdownCount + "/" + Case4Keywords.Count +
                      " спецификаций упомянуто");
        if (!feedbackOk)
            notes.Add("нет блока обратной связи из стартового блока (маркеров " + feedbackMarkers + ")");

        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count +
                     "; filter evidence " + filterEvidence + "/" + filterSchedules.Count +
                     ", element evidence " + (summarizeUsed ? "summarize" :
                         queryCategories.Count == 0 ? "нет" : "query " + queryCategories.Count) +
                     ", breakdown " + breakdownCount + "/" + Case4Keywords.Count +
                     ", diagnosis " + (diagnosisMentioned ? "да" : "нет") +
                     ", feedback " + (feedbackOk ? "да" : "нет") +
                     (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty);

        var evidenceOk = filterEvidence == filterSchedules.Count &&
                         (summarizeUsed || wallsCovered && emptyCovered >= 2);
        var structureOk = breakdownCount >= 4 && diagnosisMentioned;

        if (redFlags.Count == 0 && evidenceOk && structureOk && feedbackOk)
            return new CaseVerdict(true, 1.0, detail, redFlags);

        if (redFlags.Count == 0 && (filterEvidence > 0 || summarizeUsed) &&
            (breakdownCount >= 2 || diagnosisMentioned))
            return new CaseVerdict(false, 0.5, detail, redFlags);

        return new CaseVerdict(false, 0.0, detail, redFlags);
    }

    /// <summary>scheduleId/scheduleName из аргументов вызова (сырой JSON).</summary>
    private static List<string> ScheduleTargets(string argumentsJson)
    {
        var result = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;
            if (root.TryGetProperty("scheduleId", out var id) &&
                (id.ValueKind == JsonValueKind.Number || id.ValueKind == JsonValueKind.String))
                result.Add(id.ToString());
            if (root.TryGetProperty("scheduleName", out var name) &&
                name.ValueKind == JsonValueKind.String &&
                name.GetString() is { Length: > 0 } nameValue)
                result.Add(nameValue);
            if (root.TryGetProperty("name", out var altName) &&
                altName.ValueKind == JsonValueKind.String &&
                altName.GetString() is { Length: > 0 } altValue)
                result.Add(altValue);
        }
        catch (JsonException)
        {
            // Аргументы не парсятся — цели не извлечь.
        }

        return result;
    }

    private string? ResolveCase4ScheduleName(string target)
    {
        foreach (var seed in _case4Schedules)
        {
            if (string.Equals(seed.Name, target, StringComparison.OrdinalIgnoreCase))
                return seed.Name;
            if (long.TryParse(target, out var id) && id == seed.Id)
                return seed.Name;
            if (seed.Id.ToString().Equals(target, StringComparison.Ordinal))
                return seed.Name;
        }

        // Частичное совпадение по имени: модель может передать имя с typo.
        return _case4Schedules
            .FirstOrDefault(s => ContainsText(s.Name, target) || ContainsText(target, s.Name))
            ?.Name;
    }

    private static bool TryGetQueryCategories(string argumentsJson, out List<string> categories)
    {
        categories = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (document.RootElement.TryGetProperty("categories", out var array) &&
                array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String &&
                        item.GetString() is { Length: > 0 } value)
                        categories.Add(value);
                }

                return true;
            }

            if (document.RootElement.TryGetProperty("category", out var single) &&
                single.ValueKind == JsonValueKind.String &&
                single.GetString() is { Length: > 0 } singleValue)
            {
                categories.Add(singleValue);
                return true;
            }
        }
        catch (JsonException)
        {
            // Аргументы не парсятся — считаем, что категорий нет.
        }

        return false;
    }

    private static ElementId FirstWallTypeId(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .WhereElementIsElementType()
            .FirstElementId();
    }
}

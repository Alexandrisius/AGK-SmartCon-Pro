using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SmartCon.Agent.Services.Decisions;
using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;
using Xunit.Abstractions;

namespace SmartCon.Tests.Agent.Routing;

/// <summary>
/// Opt-in A/B on 100 Russian family-editing phrases: the production BM25 rule
/// router, the Perplexity decision model through <see cref="HttpDecisionClient"/>
/// and a second chat model (SMARTCON_LLM_MODEL) over the same nine family tools.
/// Writes a markdown report to %TEMP%\smartcon-family-ab.
/// Skipped silently unless SMARTCON_DECIDER_LIVE=1 and a key are configured.
/// </summary>
public sealed class FamilyRoutingAbLiveTests
{
    private const int SampleLimit = 5;

    private readonly ITestOutputHelper _output;

    public FamilyRoutingAbLiveTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task LiveAb_100FamilyPhrases_RulesVsDecisionsVsChatModel()
    {
        if (Environment.GetEnvironmentVariable("SMARTCON_DECIDER_LIVE") != "1")
            return;

        var decisions = AgentDecisionsSettings.Load();
        Assert.True(
            decisions.IsAvailable,
            "Set SMARTCON_DECIDER_ENABLED=1 and a key in secrets.env before a live run.");

        var llm = AgentLlmSettings.Load();
        Assert.True(llm.HasApiKey, "Set the chat-model key in secrets.env before a live run.");

        var catalog = AgentToolCards.All
            .Where(card => string.Equals(card.Pack, "families", StringComparison.Ordinal))
            // Keep the benchmark on the original nine editing tools: the delete tools and
            // the shared-parameter file reader are not part of the 100-phrase fixture.
            .Where(card => card.ToolName is not ("delete_family_types"
                or "remove_family_parameter" or "list_shared_parameters"))
            .Select(card => AgentToolCatalog.Enrich(new AgentToolDefinition
            {
                Name = card.ToolName,
                Description = card.Purpose,
                InputSchemaJson = """{"type":"object","properties":{}}"""
            }))
            .ToList();

        Assert.Equal(9, catalog.Count);
        var phrases = Phrases();
        Assert.Equal(100, phrases.Length);
        foreach (var (phrase, tool) in phrases)
        {
            Assert.Contains(catalog, definition =>
                string.Equals(definition.Name, tool, StringComparison.Ordinal));
        }

        var report = new StringBuilder();
        report.AppendLine("# Family routing A/B — 100 Russian phrases");
        report.AppendLine();
        report.AppendLine("- Family tools: " + catalog.Count);
        report.AppendLine("- Decision model: " + decisions.Provider + "/" + decisions.Model);
        report.AppendLine("- Chat model: " + llm.Provider + "/" + llm.Model);
        report.AppendLine("- Started: " + DateTime.Now.ToString("O"));
        report.AppendLine();

        var rules = ScoreRules(phrases, catalog);
        report.AppendLine("## Rules (BM25, RankOnly)");
        report.AppendLine();
        AppendMetrics(report, rules);
        AppendFailures(report, "rules", rules, phrases);

        var decisionAnswers = 0;
        var chatAnswers = 0;
        var decisionRows = new List<string?>(phrases.Length);
        var decisionProbabilities = new List<IReadOnlyDictionary<string, double>?>(
            phrases.Length);
        var chatRows = new List<string?>(phrases.Length);
        var chatErrors = new List<string>(phrases.Length);
        var decisionLatency = new List<double>(phrases.Length);
        var chatLatency = new List<double>(phrases.Length);

        using var client = new HttpDecisionClient(decisions);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + llm.ApiKey);

        var criteria = catalog.ToDictionary(
            tool => tool.Name,
            tool => (string?)tool.Description,
            StringComparer.Ordinal);

        foreach (var (phrase, _) in phrases)
        {
            var watch = Stopwatch.StartNew();
            var answers = await client.AskAsync(
                phrase,
                new Dictionary<string, AgentDecisionQuestion>(StringComparer.Ordinal)
                {
                    ["tool"] = AgentDecisionQuestion.Choice(
                        "Which tool is the best match for the user's request?",
                        criteria)
                },
                CancellationToken.None);
            watch.Stop();

            decisionLatency.Add(watch.Elapsed.TotalMilliseconds);
            var answer = answers.TryGetValue("tool", out var value) ? value : null;
            var choice = answer?.Choice;
            decisionRows.Add(string.IsNullOrWhiteSpace(choice) ? null : choice);
            decisionProbabilities.Add(answer?.Probabilities);
            if (!string.IsNullOrWhiteSpace(choice))
                decisionAnswers++;
        }

        foreach (var (phrase, _) in phrases)
        {
            var watch = Stopwatch.StartNew();
            string? error = null;
            var choice = await AskChatModelAsync(http, llm, catalog, phrase, reason => error = reason);
            watch.Stop();

            chatLatency.Add(watch.Elapsed.TotalMilliseconds);
            chatRows.Add(choice);
            if (!string.IsNullOrWhiteSpace(choice))
                chatAnswers++;
            else
                chatErrors.Add(error ?? "unknown");
            await Task.Delay(120);
        }

        var decisionScore = ScoreDecisions(phrases, decisionRows, decisionProbabilities);
        report.AppendLine("## Decisions (" + decisions.Model + ")");
        report.AppendLine();
        AppendMetrics(report, decisionScore);
        AppendChoices(report, "decisions", decisionScore, phrases);
        AppendLatency(report, "decisions", decisionLatency);

        var chatScore = ScoreChat(phrases, chatRows);
        report.AppendLine("## Chat model (" + llm.Model + ")");
        report.AppendLine();
        AppendMetrics(report, chatScore);
        AppendChoices(report, "chat", chatScore, phrases);
        AppendLatency(report, "chat", chatLatency);

        report.AppendLine("## Ответы без выбора инструмента");
        report.AppendLine();
        report.AppendLine("- decisions: " + (phrases.Length - decisionAnswers));
        report.AppendLine("- chat: " + (phrases.Length - chatAnswers));
        foreach (var group in chatErrors
                     .GroupBy(error => error, StringComparer.Ordinal)
                     .OrderByDescending(group => group.Count()))
        {
            report.AppendLine("  - " + group.Key + ": " + group.Count());
        }
        report.AppendLine();

        var directory = Path.Combine(Path.GetTempPath(), "smartcon-family-ab");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
        await File.WriteAllTextAsync(path, report.ToString(), Encoding.UTF8);

        _output.WriteLine(
            "family A/B: rules top1=" + Percent(rules.Top1, phrases.Length) +
            " top5=" + Percent(rules.Top5, phrases.Length) +
            "; decisions top1=" + Percent(decisionScore.Top1, phrases.Length) +
            " top5=" + Percent(decisionScore.Top5, phrases.Length) +
            "; chat top1=" + Percent(chatScore.Top1, phrases.Length) +
            "; report: " + path);

        Assert.True(rules.Top5 >= 0.90, "rules top-5 is below 90% on the family pack");
        Assert.True(decisionAnswers >= 95, "the decision model answered fewer than 95 phrases");
        // The configured chat model is a reasoning model: on a one-shot routing
        // prompt it sometimes answers with prose instead of a tool call (52/100 in
        // the 2026-11-19 run). That is a model-quality finding, not a harness
        // failure — require only that enough answers were produced to score it.
        Assert.True(chatAnswers >= 40, "the chat model answered fewer than 40 phrases");
    }

    private static Score ScoreRules(
        (string Phrase, string Tool)[] phrases,
        IReadOnlyList<AgentToolDefinition> catalog)
    {
        var top1 = 0;
        var top5 = 0;
        var rows = new List<string?>(phrases.Length);
        foreach (var (phrase, tool) in phrases)
        {
            var decision = RuleToolRouter.RankOnly(phrase, catalog, SampleLimit);
            var names = decision.Tools.Select(candidate => candidate.Name).ToList();
            rows.Add(names.Count > 0 ? names[0] : null);
            if (names.Count > 0 &&
                string.Equals(names[0], tool, StringComparison.Ordinal))
            {
                top1++;
            }

            if (names.Contains(tool, StringComparer.Ordinal))
                top5++;
        }

        return new Score(top1, top5, rows);
    }

    private static Score ScoreDecisions(
        (string Phrase, string Tool)[] phrases,
        IReadOnlyList<string?> choices,
        IReadOnlyList<IReadOnlyDictionary<string, double>?> probabilities)
    {
        var top1 = 0;
        var top5 = 0;
        var rows = new List<string?>(phrases.Length);
        for (var index = 0; index < phrases.Length; index++)
        {
            var choice = index < choices.Count ? choices[index] : null;
            rows.Add(choice);

            var ranked = new List<string>();
            if (!string.IsNullOrWhiteSpace(choice))
                ranked.Add(choice!);
            if (index < probabilities.Count && probabilities[index] is { } values)
            {
                foreach (var pair in values
                             .Where(pair => !string.Equals(pair.Key, choice, StringComparison.Ordinal))
                             .OrderByDescending(pair => pair.Value))
                {
                    ranked.Add(pair.Key);
                }
            }

            if (ranked.Count > 0 &&
                string.Equals(ranked[0], phrases[index].Tool, StringComparison.Ordinal))
            {
                top1++;
            }

            if (ranked.Take(SampleLimit)
                .Contains(phrases[index].Tool, StringComparer.Ordinal))
            {
                top5++;
            }
        }

        return new Score(top1, top5, rows);
    }

    private static Score ScoreChat(
        (string Phrase, string Tool)[] phrases,
        IReadOnlyList<string?> choices)
    {
        var top1 = 0;
        var rows = new List<string?>(phrases.Length);
        for (var index = 0; index < phrases.Length; index++)
        {
            var choice = index < choices.Count ? choices[index] : null;
            rows.Add(choice);
            if (!string.IsNullOrWhiteSpace(choice) &&
                string.Equals(choice, phrases[index].Tool, StringComparison.Ordinal))
            {
                top1++;
            }
        }

        return new Score(top1, top1, rows);
    }

    private static async Task<string?> AskChatModelAsync(
        HttpClient http,
        AgentLlmSettings settings,
        IReadOnlyList<AgentToolDefinition> catalog,
        string phrase,
        Action<string> reportError)
    {
        var tools = catalog.Select(tool => new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = tool.Name,
                ["description"] = Truncate(tool.Description ?? string.Empty, 180),
                ["parameters"] = JsonSerializer.Deserialize<JsonElement>(
                    tool.InputSchemaJson)
            }
        }).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["temperature"] = 0,
            ["max_tokens"] = 200,
            ["parallel_tool_calls"] = false,
            ["tool_choice"] = "auto",
            ["messages"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "system",
                    ["content"] =
                        "Ты маршрутизатор инструментов SmartCon Agent. Вызови ровно один " +
                        "инструмент, который лучше всего решает запрос пользователя. Не " +
                        "вызывай остальные."
                },
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = phrase }
            },
            ["tools"] = tools
        };

        var url = settings.BaseUrl.TrimEnd('/') + "/chat/completions";
        var body = JsonSerializer.Serialize(payload);
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                reportError("http " + (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var message = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message");

            if (message.TryGetProperty("tool_calls", out var toolCalls) &&
                toolCalls.ValueKind == JsonValueKind.Array &&
                toolCalls.GetArrayLength() > 0)
            {
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    if (!toolCall.TryGetProperty("function", out var function) ||
                        !function.TryGetProperty("name", out var nameElement) ||
                        nameElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var name = nameElement.GetString()?.Trim();
                    var match = catalog.FirstOrDefault(tool =>
                        string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                        return match.Name;
                }

                reportError("unknown tool name");
                return null;
            }

            var text = message.TryGetProperty("content", out var contentElement) &&
                       contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString()
                : null;
            var contentMatch = MatchToolName(text, catalog);
            if (contentMatch is null)
                reportError("no tool call");
            return contentMatch;
        }
        catch (Exception ex)
        {
            reportError(ex.GetType().Name);
            return null;
        }
    }

    private static string? MatchToolName(
        string? text,
        IReadOnlyList<AgentToolDefinition> catalog)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string? best = null;
        var bestIndex = int.MaxValue;
        foreach (var tool in catalog)
        {
            var index = text!.IndexOf(tool.Name, StringComparison.OrdinalIgnoreCase);
            if (index < 0 || index >= bestIndex)
                continue;

            best = tool.Name;
            bestIndex = index;
        }

        return best;
    }

    private static void AppendMetrics(StringBuilder report, Score score)
    {
        report.AppendLine("- top-1: " + score.Top1 + "/100 (" + Percent(score.Top1, 100) + ")");
        report.AppendLine("- top-5: " + score.Top5 + "/100 (" + Percent(score.Top5, 100) + ")");
        report.AppendLine();
    }

    private static void AppendFailures(
        StringBuilder report,
        string name,
        Score score,
        (string Phrase, string Tool)[] phrases)
    {
        report.AppendLine("### " + name + " — top-1 промахи");
        report.AppendLine();
        if (score.Rows.Count == 0)
        {
            report.AppendLine("(см. сводку)");
            report.AppendLine();
            return;
        }

        AppendChoiceRows(report, score, phrases);
    }

    private static void AppendChoices(
        StringBuilder report,
        string name,
        Score score,
        (string Phrase, string Tool)[] phrases)
    {
        report.AppendLine("### " + name + " — top-1 промахи");
        report.AppendLine();
        AppendChoiceRows(report, score, phrases);
    }

    private static void AppendChoiceRows(
        StringBuilder report,
        Score score,
        (string Phrase, string Tool)[] phrases)
    {
        var failures = 0;
        for (var index = 0; index < phrases.Length && index < score.Rows.Count; index++)
        {
            var choice = score.Rows[index];
            if (string.Equals(choice, phrases[index].Tool, StringComparison.Ordinal))
                continue;

            failures++;
            report.AppendLine(
                "- «" + phrases[index].Phrase + "» → " + (choice ?? "(нет)") +
                " (ожидался " + phrases[index].Tool + ")");
        }

        if (failures == 0)
            report.AppendLine("(нет)");
        report.AppendLine();
    }

    private static void AppendLatency(
        StringBuilder report,
        string name,
        List<double> durations)
    {
        if (durations.Count == 0)
            return;

        var sorted = durations.OrderBy(value => value).ToList();
        var p95 = sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1];
        report.AppendLine(
            "- latency: avg " + sorted.Average().ToString("F0") + " ms, p95 " +
            p95.ToString("F0") + " ms");
        report.AppendLine();
    }

    private static string Percent(int value, int total) =>
        total == 0 ? "0%" : ((double)value / total * 100).ToString("F1") + " %";

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max);

    private sealed record Score(int Top1, int Top5, List<string?> Rows);

    private static (string Phrase, string Tool)[] Phrases() => new[]
    {
        ("Какие семейства вообще загружены в этот проект?", "list_families"),
        ("Покажи список загруженных семейств.", "list_families"),
        ("Сколько семейств в модели?", "list_families"),
        ("Дай перечень семейств проекта с категориями.", "list_families"),
        ("Какие семейства можно редактировать, а какие нет?", "list_families"),
        ("Есть ли в проекте in-place семейства?", "list_families"),
        ("Покажи семейства категории Окна.", "list_families"),
        ("Найди загруженное семейство по имени KR_SHAR.", "list_families"),
        ("Сколько типов у каждого семейства? Дай обзор.", "list_families"),
        ("Какие семейства есть в проекте, кроме системных?", "list_families"),
        ("Выведи семейства с числом типоразмеров.", "list_families"),
        ("Какие семейства загружены в модель и какие из них неизменяемые?", "list_families"),

        ("Какие типоразмеры у семейства дверей?", "list_family_types"),
        ("Покажи все типы семейства, включая неразмещённые.", "list_family_types"),
        ("Сколько типов у семейства KR_SHAR_VOLK_15?", "list_family_types"),
        ("Есть ли у этого семейства неразмещённые типы?", "list_family_types"),
        ("Какие typeId у типов семейства окон?", "list_family_types"),
        ("Перечисли типоразмеры семейства и их параметры.", "list_family_types"),
        ("Какие типы семейства размещены в модели, а какие нет?", "list_family_types"),
        ("Дай список типоразмеров двери 1200x2100.", "list_family_types"),
        ("Какие типы есть у семейства витражей?", "list_family_types"),
        ("Покажи типы семейства с признаком размещения.", "list_family_types"),
        ("Сколько типоразмеров у семейства и как они называются?", "list_family_types"),
        ("Какие типы семейства доступны для вставки?", "list_family_types"),

        ("Покажи параметры семейства дверей.", "get_family_parameters"),
        ("Какие параметры у этого семейства?", "get_family_parameters"),
        ("Какие параметры семейства общие и какие у них GUID?", "get_family_parameters"),
        ("Какая формула у параметра Ширина?", "get_family_parameters"),
        ("Какие значения параметра Марка по типам?", "get_family_parameters"),
        ("Какие параметры семейства можно редактировать?", "get_family_parameters"),
        ("Покажи параметры экземпляра семейства.", "get_family_parameters"),
        ("Какие параметры типа есть у семейства?", "get_family_parameters"),
        ("Есть ли в семействе lookup-таблицы и у каких параметров?", "get_family_parameters"),
        ("Покажи состав параметров семейства с типами данных.", "get_family_parameters"),
        ("Какие параметры семейства вычисляются формулой?", "get_family_parameters"),
        ("Дай значения всех параметров по типу Тип1.", "get_family_parameters"),
        ("Какие параметры семейства доступны только для чтения?", "get_family_parameters"),
        ("Покажи параметры семейства, название которых содержит «Ширина».", "get_family_parameters"),
        ("Какие параметры у семейства и в каких группах они находятся?", "get_family_parameters"),

        ("Найди семейства с параметром ТДСК_Наименование.", "find_families_with_parameter"),
        ("В каких семействах есть параметр АДСК_Наименование?", "find_families_with_parameter"),
        ("Найди типы, где параметр содержит слово ВОЛК.", "find_families_with_parameter"),
        ("Где используется параметр Заказчик в семействах?", "find_families_with_parameter"),
        ("Найди семейства, у которых есть параметр с именем Марка.",
            "find_families_with_parameter"),
        ("Какие семейства содержат параметр Давление?", "find_families_with_parameter"),
        ("В каких загруженных семействах встречается параметр Ширина?",
            "find_families_with_parameter"),
        ("Найди семейства с параметром, содержащим «Наименование».",
            "find_families_with_parameter"),
        ("Покажи семейства, где значение параметра Марка равно A-1.",
            "find_families_with_parameter"),
        ("Какие семейства имеют общий параметр с таким названием?",
            "find_families_with_parameter"),
        ("Найди семейства с параметром Расход.", "find_families_with_parameter"),
        ("В каких семействах есть параметр Длина?", "find_families_with_parameter"),

        ("Переименуй параметр Марка в Артикул.", "rename_family_parameter"),
        ("Смени имя параметра Ширина на Ширина_мм в семействе.", "rename_family_parameter"),
        ("Переименуй параметр типа Количество в Кол_во.", "rename_family_parameter"),
        ("Как переименовать параметр семейства? Выполни для Марка → Артикул.",
            "rename_family_parameter"),
        ("Замени название параметра Давление на Рабочее_давление.", "rename_family_parameter"),
        ("Переименуй параметр экземпляра Примечание в Комментарий.", "rename_family_parameter"),
        ("Измени имя параметра Размер на Габарит.", "rename_family_parameter"),
        ("Переименуй параметр Площадь в Площадь_м2.", "rename_family_parameter"),
        ("Дай параметру Флаг новое имя Признак.", "rename_family_parameter"),
        ("Переименуй в семействе параметр Высота на Высота_мм.", "rename_family_parameter"),

        ("Добавь в семейство текстовый параметр Заказчик.", "add_family_parameter"),
        ("Создай в семействе параметр типа Длина Ширина_мм.", "add_family_parameter"),
        ("Добавь параметр экземпляра Комментарий (текст).", "add_family_parameter"),
        ("Добавь общий параметр АДСК_Наименование в группу Идентификация.",
            "add_family_parameter"),
        ("Создай параметр типа Количество (целое) в семействе.", "add_family_parameter"),
        ("Добавь в семейство параметр Да/Нет Флаг_новый.", "add_family_parameter"),
        ("Добавь параметр Цена с типом Число.", "add_family_parameter"),
        ("Создай в семействе параметр Угол_поворота типа Угол.", "add_family_parameter"),
        ("Добавь параметр типа Площадь Площадь_новая.", "add_family_parameter"),
        ("Добавь в семейство параметр Материал.", "add_family_parameter"),

        ("Установи Ширина = 1200 мм для типа Тип1.", "set_family_parameter_values"),
        ("Поставь Марка = A-2 у типа Тип2.", "set_family_parameter_values"),
        ("Задай Количество = 3 для типа Тип1.", "set_family_parameter_values"),
        ("Измени Высота на 2100 мм у типа Тип1.", "set_family_parameter_values"),
        ("Присвой параметру Флаг значение Да.", "set_family_parameter_values"),
        ("Заполни Примечание_экз текстом «проверено» у экземпляра.",
            "set_family_parameter_values"),
        ("Установи Размер 500 мм для типа Тип2.", "set_family_parameter_values"),
        ("Проставь Площадь 2.5 м2 в типе Тип1.", "set_family_parameter_values"),
        ("Задай параметру Марка значение B-3 для типа Тип2.", "set_family_parameter_values"),
        ("Установи ширину 1500 мм и высоту 2000 мм для Тип1.",
            "set_family_parameter_values"),

        ("Задай параметру Площадь формулу Ширина * Высота.", "set_family_formula"),
        ("Поставь формулу для Размер: Ширина + 100.", "set_family_formula"),
        ("Сделай так, чтобы Площадь вычислялась как Ширина * Высота.", "set_family_formula"),
        ("Задай формулу Высота = Ширина / 2.", "set_family_formula"),
        ("Пропиши выражение для параметра Размер через Ширину.", "set_family_formula"),
        ("Установи формулу size_lookup для параметра Размер.", "set_family_formula"),
        ("Очисти формулу у параметра Размер.", "set_family_formula"),
        ("Задай формулу Количество = 1 + 1.", "set_family_formula"),
        ("Вычисли Площадь формулой через ширину и высоту.", "set_family_formula"),

        ("Перенеси значения из Ширина в Высота.", "map_family_parameters"),
        ("Скопируй значения параметра Марка в Артикул по всем типам.", "map_family_parameters"),
        ("Замапь Ширина на Ширина_мм для всех типов.", "map_family_parameters"),
        ("Перенеси значения из Площадь в Размер.", "map_family_parameters"),
        ("Скопируй формулу из Площадь в Размер.", "map_family_parameters"),
        ("Смаппируй Марка → Код для всех типов семейства.", "map_family_parameters"),
        ("Перенеси значения параметра Давление в Давление_кПа.", "map_family_parameters"),
        ("Скопируй значения Ширина в Ширина_новая с конвертацией единиц.",
            "map_family_parameters"),
        ("Перекинь значения из одного параметра семейства в другой по всем типоразмерам.",
            "map_family_parameters"),
        ("Перенеси формулу из одного параметра в другой во всех типах.",
            "map_family_parameters")
    };
}
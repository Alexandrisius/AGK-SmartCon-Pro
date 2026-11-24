namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Permanent pins of the benchmark verdictor parsing (review r1 of the B1
/// package): the answer-string variants are fed directly to the internal
/// verdictor helpers — the mixed filled/empty summary of case 8 (P2-1), the
/// exact culprit set (P3-2), the per-family replace path of the batch
/// criterion (P3-3), the token-boundary suspect pair with a unified
/// Cyrillic/Latin rule (P3-4) and the number-bearing format justification
/// (P3-5). The tests check the PARSING, they do not re-implement it; all
/// numbers and family names are derived from the seed truth helpers.
/// No LLM, no Revit API — pure helpers.
/// </summary>
public sealed class AgentRealCasesBenchmarkVerdictTests
{
    private static string Case13DualFormatSummary()
    {
        // «заполнено N из M, пустых K из M» — вторая форма сводки кейса 8
        // (первая — «пустых K из M» по параметру). Числа — из истины сида.
        var (total, nameEmpty, markEmpty, unitEmpty) = AgentRealCasesBenchmarkTests.Case13TruthShape();
        return
            "Наименование: заполнено " + (total - nameEmpty) + " из " + total +
            ", пустых " + nameEmpty + " из " + total + "\n" +
            "Марка: заполнено " + (total - markEmpty) + " из " + total +
            ", пустых " + markEmpty + " из " + total + "\n" +
            "Единица измерения: заполнено " + (total - unitEmpty) + " из " + total +
            ", пустых " + unitEmpty + " из " + total + "\n" +
            "Количество: заполнено " + total + " из " + total;
    }

    [Test]
    public async Task Case13_SummaryMixedFilledEmptyFormat_Accepted()
    {
        // P2-1: корректная сводка «заполнено 10 из 12, пустых 2 из 12» не
        // должна отбрасываться — сегмент с числом пустых без стема параметра
        // не единственный носитель числа.
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerSummaryMatchesTruth(
                Case13DualFormatSummary()))
            .IsTrue();
    }

    [Test]
    public async Task Case13_SummaryEmptyWordForms_Accepted()
    {
        // Формы «N из M пустых» и «пустых N из M» — число пустых смотря на
        // слово «пустых» (до или после пары).
        var (total, nameEmpty, markEmpty, unitEmpty) = AgentRealCasesBenchmarkTests.Case13TruthShape();
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerSummaryMatchesTruth(
                "Наименование: " + nameEmpty + " из " + total + " пустых\n" +
                "Марка: пустых " + markEmpty + " из " + total + "\n" +
                "Единица измерения: " + unitEmpty + " из " + total + " пустых\n" +
                "Количество: 0 из " + total))
            .IsTrue();
    }

    [Test]
    public async Task Case13_SummaryAllTwelveOfTwelve_Rejected()
    {
        // «12 из 12» — это 12, а не «2 из 12» подстрокой: целые токены.
        var (total, _, _, _) = AgentRealCasesBenchmarkTests.Case13TruthShape();
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerSummaryMatchesTruth(
                "Наименование: " + total + " из " + total + "\n" +
                "Марка: " + total + " из " + total + "\n" +
                "Единица измерения: " + total + " из " + total + "\n" +
                "Количество: " + total + " из " + total))
            .IsFalse();
    }

    [Test]
    public async Task Case13_SummaryBareFilledCount_Rejected()
    {
        // Голое «10 из 12» без слова «пустых»/«заполнено» = 10 пустых
        // (буква кейса: «сколько пустых из скольких»), а не 2.
        var (total, nameEmpty, markEmpty, unitEmpty) = AgentRealCasesBenchmarkTests.Case13TruthShape();
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerSummaryMatchesTruth(
                "Наименование: " + (total - nameEmpty) + " из " + total + "\n" +
                "Марка: " + markEmpty + " из " + total + "\n" +
                "Единица измерения: " + unitEmpty + " из " + total + "\n" +
                "Количество: 0 из " + total))
            .IsFalse();
    }

    [Test]
    public async Task Case13_SummaryZeroEmptyOnName_Rejected()
    {
        var (total, _, markEmpty, unitEmpty) = AgentRealCasesBenchmarkTests.Case13TruthShape();
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerSummaryMatchesTruth(
                "Наименование: 0 из " + total + "\n" +
                "Марка: " + markEmpty + " из " + total + "\n" +
                "Единица измерения: " + unitEmpty + " из " + total + "\n" +
                "Количество: 0 из " + total))
            .IsFalse();
    }

    [Test]
    public async Task Case13_SummaryQualifierEdgeForms_Accepted()
    {
        // Квалификатор привязывается к паре пословно: скобочная приписка с
        // числом («2 из 12 (заполнено 10)») не перехватывает пару — голая
        // пара читается как пустые; «не заполнено N из M» называет пустых;
        // тире после «заполнено» не отвязывает число заполненных.
        var (total, nameEmpty, markEmpty, unitEmpty) = AgentRealCasesBenchmarkTests.Case13TruthShape();
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerSummaryMatchesTruth(
                "Наименование: " + nameEmpty + " из " + total +
                " (заполнено " + (total - nameEmpty) + ")\n" +
                "Марка: " + markEmpty + " из " + total + "\n" +
                "Единица измерения: " + unitEmpty + " из " + total + "\n" +
                "Количество: 0 из " + total))
            .IsTrue();

        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerSummaryMatchesTruth(
                "Наименование: не заполнено " + nameEmpty + " из " + total + "\n" +
                "Марка: " + markEmpty + " из " + total + "\n" +
                "Единица измерения: " + unitEmpty + " из " + total + "\n" +
                "Количество: 0 из " + total))
            .IsTrue();

        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerSummaryMatchesTruth(
                "Наименование: заполнено — " + (total - nameEmpty) + " из " + total + "\n" +
                "Марка: заполнено — " + (total - markEmpty) + " из " + total + "\n" +
                "Единица измерения: заполнено — " + (total - unitEmpty) + " из " + total + "\n" +
                "Количество: заполнено — " + total + " из " + total))
            .IsTrue();
    }

    [Test]
    public async Task Case13_CulpritsExactSet_DumpOfAllFamiliesRejected()
    {
        // P3-2: дамп ВСЕХ семейств модели как «виновников» не проходит —
        // виновники называются точным множеством (все виновники названы И
        // ни одно неповинное семейство истины не названо).
        var families = AgentRealCasesBenchmarkTests.Case13Truth()
            .Select(row => row.Family)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await Assert.That(families.Length).IsGreaterThan(
            AgentRealCasesBenchmarkTests.Case13TruthCulpritFamilies().Length);

        var dump = "Виновники: " + string.Join(", ", families);
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerNamesExactCulprits(dump))
            .IsFalse();

        var exact = "Виновники: " + string.Join(", ",
            AgentRealCasesBenchmarkTests.Case13TruthCulpritFamilies());
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerNamesExactCulprits(exact))
            .IsTrue();
    }

    [Test]
    public async Task Case13_CulpritsExactSet_InnocentExtraRejected()
    {
        // Точный список виновников + одно неповинное семейство — уже не точно.
        var culprits = AgentRealCasesBenchmarkTests.Case13TruthCulpritFamilies();
        var innocent = AgentRealCasesBenchmarkTests.Case13Truth()
            .Select(row => row.Family)
            .Distinct(StringComparer.Ordinal)
            .First(family => !culprits.Contains(family));
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerNamesExactCulprits(
                "Виновники: " + string.Join(", ", culprits) + "; без проблем: " + innocent))
            .IsFalse();
    }

    [Test]
    public async Task Case7_DirectMigrationCalls_CountsReplacePath()
    {
        // P3-3: поштучные прямые вызовы replace_family_parameter (двухшаговая
        // замена) — такой же поштучный путь миграции, как map_family_parameters.
        var calls = new List<AgentRealCasesBenchmarkTests.BenchCall>();
        for (var index = 0; index < 4; index++)
            calls.Add(new AgentRealCasesBenchmarkTests.BenchCall(
                "replace_family_parameter", "{\"dryRun\":false}", "{}", true, null, index));
        await Assert.That(AgentRealCasesBenchmarkTests.Case7DirectMigrationCalls(calls))
            .IsEqualTo(4);

        for (var index = 0; index < 4; index++)
            calls.Add(new AgentRealCasesBenchmarkTests.BenchCall(
                "map_family_parameters", "{\"dryRun\":false}", "{}", true, null, index));
        await Assert.That(AgentRealCasesBenchmarkTests.Case7DirectMigrationCalls(calls))
            .IsEqualTo(8);
    }

    [Test]
    public async Task Case7_DirectMigrationCalls_SkipsDryRunFailedAndJobs()
    {
        // dry-run preview и неуспешные вызовы миграцией не являются; пакетная
        // джоба — не прямой вызов.
        var calls = new List<AgentRealCasesBenchmarkTests.BenchCall>
        {
            new("map_family_parameters", "{\"dryRun\":true}", "{}", true, null, 0),
            new("map_family_parameters", "{\"dryRun\":false}", "{}", false, "not_found", 0),
            new("replace_family_parameter", "{\"dryRun\":true}", "{}", true, null, 0),
            new("start_job", "{\"action\":\"map_family_parameters\",\"dryRun\":false}", "{}", true, null, 0),
            new("replace_family_parameter", "{\"dryRun\":false}", "{}", true, null, 0)
        };
        await Assert.That(AgentRealCasesBenchmarkTests.Case7DirectMigrationCalls(calls))
            .IsEqualTo(1);
    }

    [Test]
    public async Task Case9_MentionsName_TokenBoundaryNotSubstring()
    {
        // P3-4: «Т11» — НЕ подстрока «Т111»: упоминание только «Т111» пару
        // подозрительных не закрывает.
        const string onlyFar = "Подозрение на разрыв: система Т111 (автодубликат).";
        await Assert.That(AgentRealCasesBenchmarkTests.Case9MentionsName(onlyFar, "Т111"))
            .IsTrue();
        await Assert.That(AgentRealCasesBenchmarkTests.Case9MentionsName(onlyFar, "Т11"))
            .IsFalse();

        const string both = "Подозрительные: Т11 и Т111 — виды не делаю.";
        await Assert.That(AgentRealCasesBenchmarkTests.Case9MentionsName(both, "Т11"))
            .IsTrue();
        await Assert.That(AgentRealCasesBenchmarkTests.Case9MentionsName(both, "Т111"))
            .IsTrue();
    }

    [Test]
    public async Task Case9_MentionsName_LatinLookalikesCounted()
    {
        // Единое правило для кириллицы и латиницы: латинские омоглифы (T11)
        // считаются упоминанием кириллического имени.
        const string latinEcho = "Suspicious pair: T11 and T111 — no views.";
        await Assert.That(AgentRealCasesBenchmarkTests.Case9MentionsName(latinEcho, "Т11"))
            .IsTrue();
        await Assert.That(AgentRealCasesBenchmarkTests.Case9MentionsName(latinEcho, "Т111"))
            .IsTrue();
    }

    [Test]
    public async Task Case9_MentionsName_DotSuffixIsNotBoundary()
    {
        // «Т1.11» — продолжение цифрового суффикса, «Т1» внутри него не
        // считается (префикс «Т11» тем более нет).
        const string dotted = "Виды по системам Т1.11 и Т2 готовы.";
        await Assert.That(AgentRealCasesBenchmarkTests.Case9MentionsName(dotted, "Т1"))
            .IsFalse();
        await Assert.That(AgentRealCasesBenchmarkTests.Case9MentionsName(dotted, "Т1.11"))
            .IsTrue();
    }

    [Test]
    public async Task Case9_SystemsListed_LatinEchoCounts()
    {
        // Единое правило: список систем латиницей зачитывается к утверждению.
        await Assert.That(AgentRealCasesBenchmarkTests.Case9SystemsListed(
                "Предлагаю виды по: T1, T2, T1.11. Подозрение: T11/T111."))
            .IsEqualTo(5);
    }

    [Test]
    public async Task Case9_HeartComplete_AllFlangesAndPumpsFilled_Accepted()
    {
        // Сердце кейса: полный PASS — только когда ВСЕ фланцы и ВСЕ насосы
        // с непустым параметром (passA r20261123: ложный PASS при фланцах 0/2).
        await Assert.That(AgentRealCasesBenchmarkTests.Case9HeartComplete(
                true, 2, 2, 2, 2, out _))
            .IsTrue();
    }

    [Test]
    public async Task Case9_HeartComplete_EmptyFlangesOrPumps_RejectedWithCounts()
    {
        // Причина отказа обязана называть фактические счётчики (detail вердикта).
        await Assert.That(AgentRealCasesBenchmarkTests.Case9HeartComplete(
                true, 2, 0, 2, 0, out var reason))
            .IsFalse();
        await Assert.That(reason).Contains("фланцы с параметром 0/2");
        await Assert.That(reason).Contains("насосы 0/2");

        await Assert.That(AgentRealCasesBenchmarkTests.Case9HeartComplete(
                true, 2, 1, 2, 2, out reason))
            .IsFalse();
        await Assert.That(reason).Contains("1/2");

        await Assert.That(AgentRealCasesBenchmarkTests.Case9HeartComplete(
                true, 2, 2, 2, 1, out reason))
            .IsFalse();
        await Assert.That(reason).Contains("насосы 1/2");
    }

    [Test]
    public async Task Case9_HeartComplete_NonSeededOrDegenerateSeed_Rejected()
    {
        // SEED DEGRADED (C4-04): без засидированных фланцев сердце не выполнено,
        // даже если насосы заполнены; вырожденный сид «0 из 0» не считается.
        await Assert.That(AgentRealCasesBenchmarkTests.Case9HeartComplete(
                false, 0, 0, 2, 2, out var reason))
            .IsFalse();
        await Assert.That(reason).Contains("не засидированы");

        await Assert.That(AgentRealCasesBenchmarkTests.Case9HeartComplete(
                true, 0, 0, 2, 2, out reason))
            .IsFalse();
        await Assert.That(reason).Contains("фланцев 0");

        await Assert.That(AgentRealCasesBenchmarkTests.Case9HeartComplete(
                true, 2, 2, 0, 0, out reason))
            .IsFalse();
        await Assert.That(reason).Contains("насосов 0");
    }

    [Test]
    public async Task Case11_FormatJustification_RequiresNumberOrSheetSize()
    {
        // P3-5: голое слово «кратн»/«влез» обоснованием не является.
        await Assert.That(AgentRealCasesBenchmarkTests.Case11AnswerJustifiesFormat(
                "Кратность не трогал, листы собрал."))
            .IsFalse();
        await Assert.That(AgentRealCasesBenchmarkTests.Case11AnswerJustifiesFormat(
                "Вид большой, но всё влезло."))
            .IsFalse();
        await Assert.That(AgentRealCasesBenchmarkTests.Case11AnswerJustifiesFormat(
                "Собрал без вопросов."))
            .IsFalse();
    }

    [Test]
    public async Task Case11_FormatJustification_NumberOrSheetSizeAccepted()
    {
        // Число-кратность и/или размер листа — осмысленное обоснование.
        await Assert.That(AgentRealCasesBenchmarkTests.Case11AnswerJustifiesFormat(
                "Кратность 1: трасса Т1 целиком помещается в базовый формат."))
            .IsTrue();
        await Assert.That(AgentRealCasesBenchmarkTests.Case11AnswerJustifiesFormat(
                "Кратность размера = 2 — взял увеличенный лист."))
            .IsTrue();
        await Assert.That(AgentRealCasesBenchmarkTests.Case11AnswerJustifiesFormat(
                "Базовый формат А3 достаточен, ничего не торчит."))
            .IsTrue();
        await Assert.That(AgentRealCasesBenchmarkTests.Case11AnswerJustifiesFormat(
                "Лист 594×420 умещает вид с запасом."))
            .IsTrue();
    }
}

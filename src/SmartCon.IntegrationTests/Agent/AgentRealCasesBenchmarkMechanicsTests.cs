using System.Text.Json;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Non-live pins of the benchmark harness mechanics introduced by the B1
/// package: artifact field limiting (C2-13 — full values up to the limit,
/// longer ones stay PARSEABLE JSON), steering thresholds (C7-03), the
/// start_job unit counter of case 2 (C2-09), the approval-list check of case
/// 4 (C4-05) and the donor documentId routing counters of case 9 (C9-06).
/// No LLM, no Revit API — pure helpers.
/// </summary>
public sealed class AgentRealCasesBenchmarkMechanicsTests
{
    [Test]
    public async Task ArtifactJsonField_FullUnderLimit()
    {
        var args = "{\"tool\":\"list_views\",\"fields\":[\"viewId\",\"name\"]}";
        var oversized = new List<(string RelativePath, string Text)>();
        var field = AgentRealCasesBenchmarkTests.ArtifactJsonField(
            args, "r-calls/001-args.json", oversized);
        await Assert.That(field).IsEqualTo(args);
        await Assert.That(oversized.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ArtifactJsonField_LongValueStaysParseableJson()
    {
        var inner = new string('x', AgentRealCasesBenchmarkTests.ArtifactFieldLimitChars + 5000);
        var args = "{\"data\":\"" + inner + "\"}";
        var oversized = new List<(string RelativePath, string Text)>();
        var field = AgentRealCasesBenchmarkTests.ArtifactJsonField(
            args, "r-calls/001-args.json", oversized);

        // Обёртка — валидный JSON (раньше обрыв давал «Unterminated string»).
        using var document = JsonDocument.Parse(field);
        await Assert.That(document.RootElement.GetProperty("truncated").GetBoolean()).IsTrue();
        await Assert.That(document.RootElement.GetProperty("totalLength").GetInt32())
            .IsEqualTo(args.Length);
        await Assert.That(document.RootElement.GetProperty("fullTextFile").GetString())
            .IsEqualTo("r-calls/001-args.json");
        await Assert.That(document.RootElement.GetProperty("prefix").GetString()!.Length)
            .IsEqualTo(AgentRealCasesBenchmarkTests.ArtifactFieldLimitChars);

        // Полный текст уходит файлом рядом с артефактом — без потерь:
        // префикс обёртки + хвост собираются обратно в исходный args.
        await Assert.That(oversized.Count).IsEqualTo(1);
        await Assert.That(oversized[0].Text).IsEqualTo(args);
        var restored = document.RootElement.GetProperty("prefix").GetString() +
                       args[AgentRealCasesBenchmarkTests.ArtifactFieldLimitChars..];
        using var full = JsonDocument.Parse(restored);
        await Assert.That(full.RootElement.GetProperty("data").GetString()!.Length).IsEqualTo(inner.Length);
    }

    [Test]
    public async Task ArtifactTextField_LongValueGetsExplicitMarker()
    {
        var message = new string('у', AgentRealCasesBenchmarkTests.ArtifactFieldLimitChars + 10);
        var oversized = new List<(string RelativePath, string Text)>();
        var field = AgentRealCasesBenchmarkTests.ArtifactTextField(
            message, "r-calls/001-message.txt", oversized);
        await Assert.That(field.Length).IsGreaterThan(
            AgentRealCasesBenchmarkTests.ArtifactFieldLimitChars);
        await Assert.That(field.Contains("обрезано харнессом", StringComparison.Ordinal)).IsTrue();
        await Assert.That(field.EndsWith("r-calls/001-message.txt]", StringComparison.Ordinal))
            .IsTrue();
        await Assert.That(oversized[0].Text).IsEqualTo(message);
    }

    [Test]
    public async Task OversizedFields_LandNextToArtifact()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bench-oversized-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var jsonPath = Path.Combine(directory, "r20261123-000000.json");
        await File.WriteAllTextAsync(jsonPath, "{}");
        try
        {
            var oversized = new List<(string RelativePath, string Text)>
            {
                (AgentRealCasesBenchmarkTests.OversizedFieldPath("r20261123-000000", 7, "args.json"), "{\"a\":1}"),
                (AgentRealCasesBenchmarkTests.OversizedFieldPath("r20261123-000000", 7, "message.txt"), "полный текст")
            };
            AgentRealCasesBenchmarkTests.WriteOversizedCallFields(jsonPath, oversized);

            var argsOnDisk = Path.Combine(directory, "r20261123-000000-calls", "007-args.json");
            var messageOnDisk = Path.Combine(directory, "r20261123-000000-calls", "007-message.txt");
            await Assert.That(File.Exists(argsOnDisk)).IsTrue();
            await Assert.That(File.Exists(messageOnDisk)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(messageOnDisk)).IsEqualTo("полный текст");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup.
            }
        }
    }

    [Test]
    public async Task SteeringThresholds_Round80PercentTime75Percent()
    {
        await Assert.That(AgentRealCasesBenchmarkTests.SteeringRoundThreshold(48))
            .IsEqualTo(39);
        await Assert.That(AgentRealCasesBenchmarkTests.SteeringTimeThresholdSeconds(900))
            .IsEqualTo(675);
        // Малые бюджеты не дают нулевых порогов.
        await Assert.That(AgentRealCasesBenchmarkTests.SteeringRoundThreshold(1))
            .IsEqualTo(1);
        await Assert.That(AgentRealCasesBenchmarkTests.SteeringTimeThresholdSeconds(60))
            .IsEqualTo(45);
    }

    [Test]
    public async Task Case7JobUnitHint_ParsesExplicitSelection()
    {
        await Assert.That(AgentRealCasesBenchmarkTests.Case7JobUnitHintCount(
                "{\"action\":\"map_family_parameters\",\"select\":{\"familyNames\":[\"А\",\"Б\",\"В\"]}}"))
            .IsEqualTo(3);
        await Assert.That(AgentRealCasesBenchmarkTests.Case7JobUnitHintCount(
                "{\"action\":\"replace_family_parameter\",\"select\":{\"familyIds\":[42]}}"))
            .IsEqualTo(1);
        await Assert.That(AgentRealCasesBenchmarkTests.Case7JobUnitHintCount(
                "{\"action\":\"set_parameters\",\"limit\":1}"))
            .IsEqualTo(1);
        // Неявная селекция (вся категория) одиночной джобой не бывает.
        await Assert.That(AgentRealCasesBenchmarkTests.Case7JobUnitHintCount(
                "{\"action\":\"map_family_parameters\"}"))
            .IsEqualTo(-1);
        await Assert.That(AgentRealCasesBenchmarkTests.Case7JobUnitHintCount("не json"))
            .IsEqualTo(-1);
    }

    [Test]
    public async Task Case9ApprovalList_CountsSystemNamesOnBoundaries()
    {
        await Assert.That(AgentRealCasesBenchmarkTests.Case9SystemsListed(
                "Предлагаю виды по: Т1, Т2, Т1.11. Подозрение: Т11/Т111."))
            .IsEqualTo(5);
        // «Т1» внутри «Т11»/«Т1.11» не считается — границы имён уважаются.
        await Assert.That(AgentRealCasesBenchmarkTests.Case9SystemsListed(
                "Подозрительные: Т11 и Т111 — виды делать не буду."))
            .IsEqualTo(2);
        await Assert.That(AgentRealCasesBenchmarkTests.Case9SystemsListed(
                "Я всё сделал, вот отчёт."))
            .IsEqualTo(0);
    }

    [Test]
    public async Task Case14DonorRouting_Counters()
    {
        const string openResult =
            "{\"success\":true,\"message\":\"opened\",\"data\":{\"documentId\":\"donor-123\",\"isActive\":false}}";
        await Assert.That(AgentRealCasesBenchmarkTests.Case14DocumentIdOfResult(openResult))
            .IsEqualTo("donor-123");
        await Assert.That(AgentRealCasesBenchmarkTests.Case14DocumentIdOfResult(null))
            .IsNull();

        await Assert.That(AgentRealCasesBenchmarkTests.Case14ArgsRouteToDocument(
                "{\"fields\":[\"name\"],\"documentId\":\"donor-123\"}", "donor-123"))
            .IsTrue();
        await Assert.That(AgentRealCasesBenchmarkTests.Case14ArgsRouteToDocument(
                "{\"fields\":[\"name\"],\"documentId\":\"other\"}", "donor-123"))
            .IsFalse();
        await Assert.That(AgentRealCasesBenchmarkTests.Case14ArgsRouteToDocument(
                "{\"fields\":[\"name\"]}", "donor-123"))
            .IsFalse();
    }

    [Test]
    public async Task Case13ManualSummary_StrictNumberAttribution()
    {
        // ORCH-4: сводка разбирается по сегментам с точным числом, а не
        // подстрокой: «12 из 12» — это 12 пустых, а не «2 из 12».
        const string twelveEmpty =
            "Наименование: 12 из 12 пустых\n" +
            "Марка: 1 из 12\n" +
            "Единица измерения: 4 из 12\n" +
            "Количество: 0 из 12";
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerSummaryMatchesTruth(twelveEmpty))
            .IsFalse();

        // Числа не из истины сида — не зачитываются.
        const string wrongNumbers =
            "Наименование: 5 из 12; Марка: 1 из 12; " +
            "Единица измерения: 4 из 12; Количество: 0 из 12";
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerSummaryMatchesTruth(wrongNumbers))
            .IsFalse();

        // «заполнено 10 из 12» — число 10, а не «0 из 12»/«1 из 12» подстрокой.
        const string filledCounts =
            "Наименование: заполнено 10 из 12; Марка: 11 из 12; " +
            "Единица измерения: 8 из 12; Количество: 12 из 12";
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerSummaryMatchesTruth(filledCounts))
            .IsFalse();

        // Построчная таблица с числами истины — зачитывается.
        const string deepseekStyle =
            "| Наименование | 2 из 12 | 10 |\n" +
            "| Марка | 1 из 12 | 11 |\n" +
            "| Единица измерения | 0 из 12 (было 4) | 12 |\n" +
            "| Количество | 0 из 12 | 12 |";
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerSummaryMatchesTruth(deepseekStyle))
            .IsTrue();

        // Компактная одна строка: атрибуция по сегментам (параметр в сегменте
        // рядом с «N из M»).
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerSummaryMatchesTruth(
                "Сводка: 2/12 пустых Наименований, 1/12 марок, 4/12 единиц, 0/12 количеств"))
            .IsTrue();

        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerSummaryMatchesTruth(
                "Я всё заполнил, всё хорошо."))
            .IsFalse();
    }

    [Test]
    public async Task Case13Culprits_DerivedFromSeedTruth()
    {
        // ORCH-4: список виновников выводится из истины сида, не из литералов
        // вердиктора; ответ обязан назвать КАЖДОГО виновника.
        var culprits = AgentRealCasesBenchmarkTests.Case13TruthCulpritFamilies();
        await Assert.That(culprits.Length).IsGreaterThanOrEqualTo(4);

        var full = "Виновники: " + string.Join(", ", culprits);
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerNamesAllCulprits(full))
            .IsTrue();

        var missingOne = string.Join(", ", culprits.Skip(1));
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerNamesAllCulprits(missingOne))
            .IsFalse();
        await Assert.That(AgentRealCasesBenchmarkTests.Case13AnswerNamesAllCulprits(
                "Нашёл дырки у " + culprits[0]))
            .IsFalse();
    }

    [Test]
    public async Task Case11SheetSystem_TokenBoundaryNotSubstring()
    {
        // ORCH-5: «Т2» больше не извлекается из «Система Т21».
        await Assert.That(AgentRealCasesBenchmarkTests.Case11SystemOfText("Система Т21"))
            .IsNull();
        await Assert.That(AgentRealCasesBenchmarkTests.Case11SystemOfText("Лист Т1 — отопление"))
            .IsEqualTo("Т1");
        await Assert.That(AgentRealCasesBenchmarkTests.Case11SystemOfText("Т1.11 канализация"))
            .IsEqualTo("Т1.11");
        await Assert.That(AgentRealCasesBenchmarkTests.Case11SystemOfText("Т2"))
            .IsEqualTo("Т2");
    }
}

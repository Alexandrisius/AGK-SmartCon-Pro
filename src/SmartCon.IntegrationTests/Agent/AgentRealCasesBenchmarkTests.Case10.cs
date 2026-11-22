using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Agent.Services.Decisions;
using SmartCon.Agent.Services.Tools;
using SmartCon.Agent.Services.Tools.Semantic;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 10 of the real-cases benchmark (v2 wave, SSOT case 5 «Классификатор»):
/// a ~110-line hierarchical classifier txt sits next to the model; the model
/// must fill the type-level «Код по классификатору» (Assembly Code,
/// UNIFORMAT_CODE) on every seeded family — one code per family (both ball
/// valve types), but three DIFFERENT codes for the system pipe types
/// Сталь/Медь/ПВХ. Семейства создаются из family-шаблонов установки
/// (библиотека .rfa этой машины MEP-семейств не содержит) — семантика живёт
/// в именах семейств/типов. The verdict re-reads the code of every seeded
/// type id and checks breadcrumbs in the answer, honest uncertainty
/// reporting, no invented codes and (bonus metric, not a blocker) context
/// economy via semantic_match instead of reading the whole classifier into
/// the chat. Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // Дословно из SSOT (docs/agent/real-cases-benchmark-v2.md, кейс 5).
    private const string Case10Text =
        "К модели подключён классификатор — иерархический текстовый файл рядом с моделью " +
        "(razdel › подраздел › элемент, коды вида \"30.20.10\"). Заполни параметр " +
        "\"Код по классификатору\" на уровне типоразмера у всех семейств модели: проанализируй " +
        "каждое семейство — категорию, имя, значения общих и системных параметров, имя " +
        "типоразмера — и подбери самый подходящий код из классификатора. Обычно всем " +
        "типоразмерам одного семейства подходит один код, но если типы реально различаются " +
        "по классификации (например труба стальная, медная и пластиковая — это разные " +
        "типоразмеры) — у каждого типа свой код. В итоге покажи: каким семействам какие коды " +
        "присвоены и каким путём по классификатору (раздел › подраздел › элемент), и " +
        "отдельно — что не удалось уверенно классифицировать.";

    private const string Case10Title = "Классификатор: «Код по классификатору» по атрибутам семейств";

    // Ни одного Revit-типа в свойствах (правило 1 интеграционных тестов,
    // тот же механизм, что Case4ScheduleSeed).
    private sealed class Case10TypeExpectation
    {
        public string FamilyName { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        public long TypeId { get; init; }

        public string ExpectedCode { get; init; } = string.Empty;

        /// <summary>Тип системной трубы (Сталь/Медь/ПВХ) — код свой у каждого типа.</summary>
        public bool IsPipe { get; init; }
    }

    private List<Case10TypeExpectation> _case10Types = null!;

    private List<string> _case10FamilyNames = null!;

    private int _case10BallValveTypes;

    private string? _case10ClassifierPath;

    private HashSet<string> _case10ClassifierCodes = null!;

    private int _case10ClassifierItems;

    private int _case10ClassifierLines;

    private string? _case10SeedError;

    private HttpDecisionClient? _case10Decisions;

    [Test]
    public async Task Case10_Live()
    {
        var config = DogfoodConfig.TryLoad();
        if (config is null)
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        try
        {
            SeedCase10();
            // Appendix-строка харнесса: путь к temp-файлу классификатора, созданному
            // сидом (контекст харнесса, не изменение текста кейса из SSOT).
            var caseText = Case10Text + "\n\n(Файл классификатора: " + _case10ClassifierPath + ")";
            var run = RunCase(
                10, Case10Title, caseText, config, CreateCase10Tools(),
                Case10SeedSummary(), EvaluateCase10, DumpCase10State);

            // Мягкий ассерт: прогон вообще состоялся (есть ответ модели или вызовы).
            var runHappened = run.Calls.Count > 0 || !string.IsNullOrWhiteSpace(run.Answer);
            await Assert.That(runHappened).IsTrue();
        }
        finally
        {
            CleanupCase10();
        }
    }

    /// <summary>Гейт сида для ночных прогонов без LLM: структура сида без live-гарда.</summary>
    [Test]
    public async Task Case10_SeedVerifies()
    {
        try
        {
            SeedCase10();

            await Assert.That(_case10FamilyNames.Count).IsEqualTo(Case10FamilySpecs().Length);
            await Assert.That(_case10BallValveTypes).IsGreaterThanOrEqualTo(2);
            await Assert.That(_case10Types.Count(item => item.IsPipe))
                .IsEqualTo(Case10PipeTypeNames.Length);

            var empty = _case10Types.Count(item =>
                string.IsNullOrWhiteSpace(Case10CodeOf(item)));
            await Assert.That(empty).IsEqualTo(_case10Types.Count);

            await Assert.That(_case10ClassifierLines).IsGreaterThanOrEqualTo(100);
            foreach (var code in _case10Types.Select(item => item.ExpectedCode).Distinct())
                await Assert.That(_case10ClassifierCodes.Contains(code)).IsTrue();
        }
        finally
        {
            CleanupCase10();
        }
    }

    // ── Сид ─────────────────────────────────────────────────────────────

    private void SeedCase10()
    {
        // MEP-шаблон установки: нужны PipeType/PipingSystemType для системной
        // трубы (паттерн AgentRelationsToolsTests). Отсутствие — только скип.
        _document = SampleFiles.NewMepTemplateDocument(Application);
        _document ??= Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasPipeTypes(Doc))
        {
            Skip.Test("В шаблоне проекта нет типов труб — сид кейса 10 невозможен");
            return;
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _case10Types = new List<Case10TypeExpectation>();
        _case10FamilyNames = new List<string>();
        _case10BallValveTypes = 0;
        _case10SeedError = null;

        // Классификатор «рядом с моделью» — temp-файл, созданный сидом.
        var directory = Path.Combine(Path.GetTempPath(), "smartcon-bench-case10");
        Directory.CreateDirectory(directory);
        _case10ClassifierPath = Path.Combine(directory, "classifier.txt");
        File.WriteAllText(_case10ClassifierPath, Case10ClassifierText, new UTF8Encoding(true));

        _transactions.RunInTransaction(Doc, "Seed case 10 (families and pipe types)", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 0.0);
            level.Name = "Этаж 1";

            foreach (var spec in Case10FamilySpecs())
            {
                var family = LoadBenchFamilyFromTemplate(doc, spec.Label, spec.Types, spec.Templates);
                if (family is null)
                {
                    _case10SeedError = "Не удалось создать семейство «" + spec.Label +
                                       "» из family-шаблона (" + string.Join("/", spec.Templates) + ")";
                    return;
                }

                var symbolIds = family.GetFamilySymbolIds();
                _case10FamilyNames.Add(family.Name);
                if (spec.Label == "Кран шаровой")
                    _case10BallValveTypes = symbolIds.Count;
                foreach (var symbolId in symbolIds)
                {
                    if (doc.GetElement(symbolId) is not FamilySymbol symbol)
                        continue;

                    Case10ClearAssemblyCode(symbol);
                    _case10Types.Add(new Case10TypeExpectation
                    {
                        FamilyName = family.Name,
                        Label = family.Name + "/" + symbol.Name,
                        TypeId = symbol.Id.GetValue(),
                        ExpectedCode = spec.ExpectedCode
                    });
                }
            }

            var systemTypeId = BenchFirstTypeId(doc, typeof(PipingSystemType));
            var pipeTypeId = BenchFirstTypeId(doc, typeof(PipeType));
            if (systemTypeId is null || pipeTypeId is null)
            {
                _case10SeedError = "PipeType/PipingSystemType исчезли после проверки шаблона";
                return;
            }

            // Системная труба: 3 типоразмера-копии piping-типа с именами
            // «Сталь»/«Медь»/«ПВХ» + по одному экземпляру трубы каждого типа
            // (типоразмеры видимы в модели через query_elements).
            var basePipeType = doc.GetElement(pipeTypeId) as ElementType;
            if (basePipeType is null)
            {
                _case10SeedError = "Базовый PipeType не читается";
                return;
            }

            var x = 0.0;
            foreach (var typeName in Case10PipeTypeNames)
            {
                ElementType copy;
                try
                {
                    copy = basePipeType.Duplicate(typeName);
                }
                catch (Exception ex)
                {
                    _case10SeedError = "Не удалось дублировать тип трубы «" + typeName +
                                       "»: " + ex.GetType().Name + ": " + ex.Message;
                    return;
                }

                Case10ClearAssemblyCode(copy);
                _case10Types.Add(new Case10TypeExpectation
                {
                    FamilyName = "(системная труба)",
                    Label = "Труба/" + copy.Name,
                    TypeId = copy.Id.GetValue(),
                    ExpectedCode = Case10PipeCodeOf(typeName),
                    IsPipe = true
                });

                Pipe.Create(doc, systemTypeId, copy.Id, level.Id,
                    new XYZ(x, 0, -2.0), new XYZ(x + 5.0, 0, -2.0));
                x += 8.0;
            }

            doc.Regenerate();
        });

        if (_case10SeedError is not null)
        {
            Skip.Test(_case10SeedError);
            return;
        }

        VerifyCase10Seed();
    }

    /// <summary>Библиотечные семейства могут нести готовые Assembly Code — сид обнуляет их.</summary>
    private static void Case10ClearAssemblyCode(Element element)
    {
        try
        {
            element.get_Parameter(BuiltInParameter.UNIFORMAT_CODE)?.Set(string.Empty);
        }
        catch (Exception)
        {
            // Параметра нет или заблокирован — проверка сида заметит непустой код.
        }
    }

    private void VerifyCase10Seed()
    {
        foreach (var expectation in _case10Types)
        {
            if (Doc.GetElement(ElementIdCompat.Create(expectation.TypeId)) is not ElementType type)
            {
                Skip.Test("Тип «" + expectation.Label + "» исчез после сида кейса 10");
                return;
            }

            var code = Case10CodeOf(expectation);
            if (code is not null)
            {
                Skip.Test("Сид кейса 10 некорректен: код «" + expectation.Label +
                          "» не пуст («" + code + "»)");
                return;
            }
        }

        if (_case10FamilyNames.Count != Case10FamilySpecs().Length)
        {
            Skip.Test("Загружено " + _case10FamilyNames.Count + " семейств вместо " +
                      Case10FamilySpecs().Length);
            return;
        }

        if (_case10BallValveTypes < 2)
        {
            Skip.Test("У шарового крана " + _case10BallValveTypes + " типоразмеров — нужно не меньше 2");
            return;
        }

        var pipeTypes = _case10Types.Count(item => item.IsPipe);
        if (pipeTypes != Case10PipeTypeNames.Length)
        {
            Skip.Test("Создано " + pipeTypes + " типов трубы вместо " + Case10PipeTypeNames.Length);
            return;
        }

        Case10ParseClassifier();
        if (_case10SeedError is not null)
            Skip.Test(_case10SeedError);
    }

    /// <summary>Разбор файла классификатора самим read_classifier-тулом (гейт формата).</summary>
    private void Case10ParseClassifier()
    {
        try
        {
            var reader = new ReadClassifierTool();
            var args = JsonSerializer.SerializeToElement(new { path = _case10ClassifierPath });
            var result = reader.ExecuteAsync(
                    new AgentToolContext(new InlineAwaitableEvent(), "seed", Guid.Empty),
                    args, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            if (!result.Success || result.Data is null)
            {
                _case10SeedError = "read_classifier не разобрал файл сида: " +
                                   (result.ErrorCode ?? "неизвестная ошибка");
                return;
            }

            var data = result.Data.Value;
            _case10ClassifierItems = data.GetProperty("total").GetInt32();
            _case10ClassifierLines = data.GetProperty("totalLines").GetInt32();
            _case10ClassifierCodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.GetProperty("items").EnumerateArray())
            {
                var code = item.GetProperty("code").GetString();
                if (code is not null)
                    _case10ClassifierCodes.Add(code);
            }

            if (_case10ClassifierLines < 100)
                _case10SeedError = "Классификатор короче 100 строк (" +
                                   _case10ClassifierLines + ") — сид некорректен";

            var missing = _case10Types
                .Select(item => item.ExpectedCode)
                .Distinct()
                .Where(code => !_case10ClassifierCodes.Contains(code))
                .ToList();
            if (missing.Count > 0)
                _case10SeedError = "В классификаторе нет кодов истины сида: " +
                                   string.Join(", ", missing);
        }
        catch (Exception ex)
        {
            _case10SeedError = "Не удалось прочитать классификатор: " +
                               ex.GetType().Name + ": " + ex.Message;
        }
    }

    private void CleanupCase10()
    {
        _case10Decisions?.Dispose();
        _case10Decisions = null;
        try
        {
            if (_case10ClassifierPath is not null && File.Exists(_case10ClassifierPath))
                File.Delete(_case10ClassifierPath);
        }
        catch (IOException)
        {
            // Temp-файл — чистка best-effort.
        }
    }

    // ── Сводка сида и тулы ──────────────────────────────────────────────

    private List<string> Case10SeedSummary()
    {
        var lines = new List<string>
        {
            "8 семейств из family-шаблонов установки (Plumbing Fixture / Mechanical Equipment / Window / Door / Column / Lighting Fixture) + системная труба, коды «Код по классификатору» (Assembly Code) изначально ПУСТЫЕ:"
        };
        foreach (var familyName in _case10FamilyNames)
        {
            var family = _case10Types.Where(item => item.FamilyName == familyName).ToList();
            lines.Add("  - «" + familyName + "» — " + family.Count +
                      " типоразмер(ов), ожидаемый код " + family[0].ExpectedCode);
        }

        foreach (var pipe in _case10Types.Where(item => item.IsPipe))
            lines.Add("  - тип трубы «" + pipe.Label + "» — ожидаемый код " + pipe.ExpectedCode);

        lines.Add("Классификатор: " + _case10ClassifierPath + " (" + _case10ClassifierLines +
                  " строк, " + _case10ClassifierItems + " кодов)");
        return lines;
    }

    private List<IAgentTool> CreateCase10Tools()
    {
        var context = new StubRevitContext(Doc);
        var decisions = AgentDecisionsSettings.Load();
        _case10Decisions = new HttpDecisionClient(decisions);

        var tools = new List<IAgentTool>
        {
            new ListFamiliesTool(context),
            new ListFamilyTypesTool(context),
            new GetElementDetailsTool(context),
            new SetParametersTool(context, _transactions!, _changeSets!),
            new ReadClassifierTool(),
            new SemanticMatchTool(_case10Decisions, decisions),
            new QueryElementsTool(context),
            new SummarizeElementsTool(context),
            new UndoLastTool(context, _transactions!, _changeSets!),
            new ListViewsTool(context),
            new ListLevelsTool(context),
            new ListCategoriesTool(context),
            new GetProjectOverviewTool(context),
            new GetCurrentViewInfoTool(context),
            new GetViewSettingsTool(context),
            new GetSelectedElementsTool(context, new Case10NoSelection())
        };

        var findTools = new FindToolsTool(new RuleToolRouter());
        findTools.Attach(new AgentToolRegistry(tools));
        tools.Add(findTools);
        return tools;
    }

    private sealed class Case10NoSelection : IAgentSelectionProvider
    {
        public IReadOnlyList<ElementId>? GetSelectedElementIds() => null;
    }

    // ── Вердикт ─────────────────────────────────────────────────────────
    // Case10CodeOf / Case10ItemsOf / Case10CountOccurrences / DumpCase10State —
    // в AgentRealCasesBenchmarkTests.Case10.Support.cs.

    private CaseVerdict EvaluateCase10(
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

        // (a) Состояние: код на каждом типоразмере сида (обоим типам крана — один код,
        // трём типам трубы — три разных).
        var total = _case10Types.Count;
        var correct = 0;
        var wrong = new List<string>();
        var invented = new List<string>();
        foreach (var item in _case10Types)
        {
            var code = Case10CodeOf(item);
            if (code is not null && code == item.ExpectedCode)
            {
                correct++;
                continue;
            }

            wrong.Add(item.Label + " = «" + (code ?? "(пусто)") + "», ожидалось «" +
                      item.ExpectedCode + "»");
            if (code is not null && !_case10ClassifierCodes.Contains(code))
                invented.Add(item.Label + ": «" + code + "» нет в классификаторе");
        }

        if (invented.Count > 0)
            redFlags.Add("выдуманы коды вне классификатора: " + string.Join("; ", invented));

        // (b) Write-дисциплина (смягчённая): одиночный set_parameters с
        // dryRun=true → dryRun=false — норма; apply без единого успешного
        // dry-run до него — красный флаг.
        var sawDryRun = false;
        foreach (var call in calls)
        {
            if (call.ToolName != "set_parameters")
                continue;

            if (IsDryRunCall(call))
            {
                if (call.Success)
                    sawDryRun = true;
                continue;
            }

            if (call.Success && !sawDryRun)
            {
                redFlags.Add("запись кодов без превью: apply set_parameters до любого успешного dry-run");
                break;
            }
        }

        // (c) Пути-крошки в ответе (раздел › подраздел › элемент), не голые коды.
        var breadcrumbs = Case10CountOccurrences(answerText, "›");
        var breadcrumbsOk = breadcrumbs >= 3;
        if (!breadcrumbsOk)
            notes.Add("в ответе нет путей по классификатору (вхождений «›»: " + breadcrumbs + ")");

        // (d) Честность: несовпавшие/пустые коды перечислены как «не удалось уверенно».
        var honestAboutUncertain = new[]
            {
                "не удалось", "неуверен", "не увер", "сомнев", "спорн", "неопредел"
            }
            .Any(marker => ContainsText(answerText, marker));
        if (wrong.Count > 0 && !honestAboutUncertain)
            notes.Add("есть несовпавшие/пустые коды (" + wrong.Count +
                      "), но нет блока «что не удалось уверенно классифицировать»");

        // (e) Бонус-метрика: экономия контекста (строки классификатора, попавшие
        // в контекст; semantic_match вместо чтения всех строк). НЕ блокер.
        var classifierLinesInContext = 0;
        var readClassifierCalls = 0;
        foreach (var call in calls)
        {
            if (call.ToolName != "read_classifier" || !call.Success)
                continue;

            readClassifierCalls++;
            classifierLinesInContext += Case10ItemsOf(call.ResultJson);
        }

        // Попытка, не только успех: при выключенном decision-слое semantic_match
        // честно отвечает decision_unavailable (success=false) — это не отказ модели.
        var semanticUsed = calls.Any(call => call.ToolName == "semantic_match");
        if (readClassifierCalls > 0 && !semanticUsed)
            notes.Add("не воспользовалась семантическим выбором: read_classifier есть, semantic_match нет (бонус-метрика)");
        if (semanticUsed && readClassifierCalls <= 1)
            notes.Add("контекст-экономия: semantic_match использован, read_classifier вызван " +
                      readClassifierCalls + " раз(а)");

        // (f) Обратная связь из стартового блока.
        var feedbackMarkers = new[] { "удобн", "неудобн", "невозможно", "не хватило" }
            .Count(marker => ContainsText(answerText, marker));
        var feedbackOk = feedbackMarkers >= 2;
        if (!feedbackOk)
            notes.Add("нет блока обратной связи из стартового блока (маркеров " + feedbackMarkers + ")");

        var ratio = total == 0 ? 0.0 : (double)correct / total;
        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count +
                     "; коды " + correct + "/" + total +
                     " (" + ratio.ToString("0%", CultureInfo.InvariantCulture) + ")" +
                     (wrong.Count > 0
                         ? "; проблемы: " + string.Join("; ", wrong.Take(6)) +
                           (wrong.Count > 6 ? " …" : string.Empty)
                         : string.Empty) +
                     "; крошки «›» " + breadcrumbs +
                     ", честность " + (wrong.Count == 0 || honestAboutUncertain ? "да" : "нет") +
                     ", семант.выбор " + (semanticUsed ? "да" : "нет") +
                     ", строк классификатора в контексте " + classifierLinesInContext +
                     (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty);

        if (redFlags.Count == 0 && correct == total && breadcrumbsOk && feedbackOk)
            return new CaseVerdict(true, 1.0, detail, redFlags);

        if (redFlags.Count == 0 && ratio >= 0.6)
            return new CaseVerdict(false, 0.5, detail, redFlags);

        return new CaseVerdict(false, 0.0, detail, redFlags);
    }
}

using System.IO;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Transactions;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 14 of the real-cases benchmark = v2 case 9 (multi-model): a donor
/// .rvt (created and saved by the seed into %TEMP%\smartcon-bench-case14)
/// holds the reference view names «В_Этаж N_Отопление/Канализация» and the
/// filled «АДСК_Марка» on 3 generic families; the active model has the same
/// 4 views under junk names («Копия 1 — 3D», «Вид 1», «Вид 2», «Черновик»)
/// and the same families with an EMPTY mark. The model must read the donor
/// INVISIBLY (open_model_background + documentId routing, AgentMultiModelTests
/// mechanics), bring its own names/marks to the donor truth and close the
/// donor — never touching its file. Путь донора даётся appendix-строкой
/// харнесса (плейсхолдер «&lt;путь к .rvt&gt;» в тексте кейса не меняется —
/// как классификатор в кейсе 10). Вердикт — в партиале Case14.Evaluation
/// (EvaluateCase14). Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // Дословно из SSOT (docs/agent/real-cases-benchmark-v2.md, кейс 9).
    private const string Case14Text =
        "Рядом лежит модель-эталон: <путь к .rvt>. Посмотри в ней, как оформлены " +
        "виды (имена по красивому паттерну) и какие значения стоят в общем " +
        "параметре «АДСК_Марка», — не открывая её у меня на экране, — и приведи " +
        "мою текущую модель к такому же виду: имена видов и значения марки должны " +
        "совпадать с эталоном. Работай аккуратно: моя модель остаётся активной, " +
        "ничего в файле эталона не меняй.";

    private const string Case14Title = "Мульти-модельность: сделай как в модели-эталоне";

    private const string Case14MarkParameter = "АДСК_Марка";

    /// <summary>Сидовый GUID общего параметра (свои GUID, не настоящий АДСК).</summary>
    private static readonly Guid Case14MarkGuid = new("b2d5f88c-3e4a-4d6b-9c0d-8e7f6a5b4c07");

    private const string Case14Level1 = "Этаж 1";
    private const string Case14Level2 = "Этаж 2";
    private const string Case14SharedGroup = "СК_Кейс14";

    /// <summary>Паттерн имён видов донора (истина для переименования).</summary>
    private static readonly string[] Case14DonorViews =
    {
        "В_Этаж 1_Отопление", "В_Этаж 2_Отопление", "В_Этаж 1_Канализация", "В_Этаж 2_Канализация"
    };

    // Истина марок (значения на ТИПАХ семейств донора) и кривые имена основной
    // модели — методы, не поля: инициализаторы с кортежами читаются в рантайме.
    private static (string Family, string Type, string Mark)[] Case14FamilyTruth() => new[]
    {
        ("Кран шаровой", "Ду15", "КШ-15"),
        ("Насос", "К 20-30", "Н-1"),
        ("Фильтр сетчатый", "Ду40", "ФС-40")
    };

    private static (string BadName, string Level)[] Case14MainViews() => new[]
    {
        ("Копия 1 — 3D", Case14Level1),
        ("Вид 1", Case14Level2),
        ("Вид 2", Case14Level1),
        ("Черновик", Case14Level2)
    };

    // Ни одного Revit-типа в свойствах (правило 1 интеграционных тестов).
    private sealed class Case14ViewSeed
    {
        public long ViewId { get; init; }

        public string OriginalName { get; init; } = string.Empty;

        public string LevelName { get; init; } = string.Empty;
    }

    private sealed class Case14TypeSeed
    {
        public string FamilyName { get; init; } = string.Empty;

        public string TypeName { get; init; } = string.Empty;

        public long TypeId { get; init; }

        public string ExpectedMark { get; init; } = string.Empty;
    }

    private List<Case14ViewSeed> _case14Views = null!;

    private List<Case14TypeSeed> _case14Types = null!;

    private SharedParameterFileScope? _case14SharedFile;

    private string? _case14DonorPath;

    private string? _case14Directory;

    /// <summary>Штамп файла донора на момент сида (неизменность сверяется вердиктом).</summary>
    private FileInfo? _case14DonorStamp;

    private string _case14ActiveIdentity = string.Empty;

    private string? _case14SeedError;

    /// <summary>Диагностика загрузки .rfa в донор (для скип-сообщения сида).</summary>
    private readonly List<string> _case14LoadReport = new();

    [Test]
    public async Task Case14_Live()
    {
        if (!LiveConfigured())
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        try
        {
            SeedOnBenchBaseLive(14, Case14Title, SeedCase14);
            // Appendix-строка харнесса: конкретный путь temp-донора (контекст
            // харнесса; текст кейса из SSOT не меняется).
            var caseText = Case14Text + "\n\n(Модель-эталон: " + _case14DonorPath + ")";
            var run = RunProductionCase(
                14, Case14Title, caseText,
                Case14SeedSummary(), EvaluateCase14, DumpCase14State);

            // Мягкий ассерт: прогон вообще состоялся (есть ответ модели или вызовы).
            var runHappened = run.Calls.Count > 0 || !string.IsNullOrWhiteSpace(run.Answer);
            await Assert.That(runHappened).IsTrue();
        }
        finally
        {
            CleanupCase14();
        }
    }

    /// <summary>Гейт сида для ночных прогонов без LLM: структура сида без live-гарда.</summary>
    [Test]
    public async Task Case14_SeedVerifies()
    {
        try
        {
            SeedOnBenchBase("case14", SeedCase14);

            await Assert.That(File.Exists(_case14DonorPath)).IsTrue();
            await Assert.That(_case14DonorStamp!.Length).IsGreaterThan(0);

            // Основная модель: 4 вида с кривыми именами, семейства с ПУСТОЙ маркой.
            await Assert.That(_case14Views.Count).IsEqualTo(4);
            foreach (var viewSeed in _case14Views)
            {
                var view = Doc.GetElement(ElementIdCompat.Create(viewSeed.ViewId)) as View;
                await Assert.That(view).IsNotNull();
                await Assert.That(view!.Name).IsEqualTo(viewSeed.OriginalName);
                await Assert.That(Case14LevelNameOf(view)).IsEqualTo(viewSeed.LevelName);
            }

            await Assert.That(_case14Types.Count).IsEqualTo(3);
            foreach (var typeSeed in _case14Types)
            {
                await Assert.That(string.IsNullOrWhiteSpace(Case14MarkOf(typeSeed.TypeId)))
                    .IsTrue();
            }

            // Донор: истина читается повторным фоновым открытием (механика
            // AgentMultiModelTests: OpenDocumentFile → коллекторы → Close).
            var donor = Application.OpenDocumentFile(_case14DonorPath!);
            try
            {
                var donorViewNames = new List<string>();
                foreach (var element in new FilteredElementCollector(donor)
                             .OfClass(typeof(View))
                             .Cast<View>()
                             .Where(view => !view.IsTemplate))
                {
                    donorViewNames.Add(element.Name);
                }

                foreach (var viewName in Case14DonorViews)
                    await Assert.That(donorViewNames.Contains(viewName)).IsTrue();

                foreach (var truth in Case14FamilyTruth())
                {
                    var symbol = Case14FindSymbol(donor, truth.Family, truth.Type);
                    await Assert.That(symbol).IsNotNull();
                    await Assert.That(symbol!.get_Parameter(Case14MarkGuid)?.AsString() ??
                                      string.Empty).IsEqualTo(truth.Mark);
                }
            }
            finally
            {
                donor.Close(false);
            }
        }
        finally
        {
            CleanupCase14();
        }
    }

    // ── Сид ─────────────────────────────────────────────────────────────

    private void SeedCase14()
    {
        _case14SeedError = null;
        _case14Views = new List<Case14ViewSeed>();
        _case14Types = new List<Case14TypeSeed>();
        _case14LoadReport.Clear();
        _case14SharedFile = SharedParameterFileScope.Create(Application);
        var definition = SeedCase14MarkDefinition();
        if (_case14SeedError is not null)
        {
            Skip.Test(_case14SeedError);
            return;
        }

        // Файлы семейств создаются ОДИН раз и грузятся в донор и в основную
        // модель одинаковыми (марки — значения проекта донора, в .rfa их нет).
        _case14Directory = Path.Combine(Path.GetTempPath(), "smartcon-bench-case14");
        Directory.CreateDirectory(_case14Directory);
        var familyPaths = CreateCase14FamilyFiles(_case14Directory);
        if (_case14SeedError is not null)
        {
            Skip.Test(_case14SeedError);
            return;
        }

        _case14DonorPath = Path.Combine(_case14Directory, "donor.rvt");
        SeedCase14Donor(familyPaths, definition!);
        if (_case14SeedError is not null)
        {
            Skip.Test(_case14SeedError);
            return;
        }

        _case14DonorStamp = new FileInfo(_case14DonorPath);

        // Основная модель: те же семейства (те же имена) с пустой маркой.
        _document = BenchDocuments.NewBase(Application);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
        {
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");
            return;
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed case 14 main (bind)", doc =>
        {
            try
            {
                // Загрузка .rfa — ВНУТРИ транзакции (LoadFamily(string) без неё
                // молча false — та же ловушка, что у донора).
                if (!Case14LoadFamilies(doc, familyPaths))
                    return;

                var level1 = ModelSeed.CreateLevel(doc, 0.0);
                level1.Name = Case14Level1;
                var level2 = ModelSeed.CreateLevel(doc, 3.0);
                level2.Name = Case14Level2;
                Case14BindMark(doc, definition!);
            }
            catch (Exception ex)
            {
                _case14SeedError = "Привязка марки в основной модели: " +
                                   ex.GetType().Name + ": " + ex.Message;
            }
        });

        if (_case14SeedError is not null)
        {
            Skip.Test(_case14SeedError);
            return;
        }

        _transactions.RunInTransaction(Doc, "Seed case 14 main (views)", doc =>
        {
            try
            {
                foreach (var (badName, levelName) in Case14MainViews())
                {
                    var level = Case14FindLevel(doc, levelName);
                    if (level is null)
                    {
                        _case14SeedError = "В основной модели нет уровня «" + levelName + "»";
                        return;
                    }

                    var view = ModelSeed.CreateViewPlanChecked(doc, level.Id, badName);
                    _case14Views.Add(new Case14ViewSeed
                    {
                        ViewId = view.Id.GetValue(),
                        OriginalName = badName,
                        LevelName = levelName
                    });
                }
            }
            catch (Exception ex)
            {
                _case14SeedError = "Виды основной модели кейса 14: " +
                                   ex.GetType().Name + ": " + ex.Message;
            }
        });

        if (_case14SeedError is not null)
        {
            Skip.Test(_case14SeedError);
            return;
        }

        foreach (var truth in Case14FamilyTruth())
        {
            var symbol = Case14FindSymbol(Doc, truth.Family, truth.Type);
            if (symbol is null)
            {
                _case14SeedError = "В основной модели нет семейства «" +
                                   truth.Family + "/" + truth.Type + "» после загрузки";
                break;
            }

            _case14Types.Add(new Case14TypeSeed
            {
                FamilyName = truth.Family,
                TypeName = truth.Type,
                TypeId = symbol.Id.GetValue(),
                ExpectedMark = truth.Mark
            });
        }

        if (_case14SeedError is not null)
        {
            Skip.Test(_case14SeedError);
            return;
        }

        _case14ActiveIdentity = AgentDocumentIdentity.GetDocumentId(Doc);
        VerifyCase14Seed();
    }

    private ExternalDefinition? SeedCase14MarkDefinition()
    {
        try
        {
            var file = Application.OpenSharedParameterFile();
            if (file is null)
            {
                _case14SeedError = "Временный ФОП кейса 14 не открылся";
                return null;
            }

            var group = file.Groups.Create(Case14SharedGroup);
            return (ExternalDefinition)group.Definitions.Create(
                new ExternalDefinitionCreationOptions(
                    Case14MarkParameter, SpecTypeId.String.Text)
                {
                    GUID = Case14MarkGuid
                });
        }
        catch (Exception ex)
        {
            _case14SeedError = "Не удалось создать определение «" + Case14MarkParameter +
                               "»: " + ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>Файлы .rfa семейств истины (FamilySeed-паттерн без загрузки в проект).</summary>
    private List<string> CreateCase14FamilyFiles(string directory)
    {
        var paths = new List<string>();
        var template = SampleFiles.FindFamilyTemplate(Application);
        if (template is null)
        {
            _case14SeedError = "В установленном Revit нет family-шаблона Generic Model (.rft)";
            return paths;
        }

        foreach (var truth in Case14FamilyTruth())
        {
            // Имя файла = имени семейства (ловушка SaveAs: файловое имя утекает
            // в имя семейства — префикс «case14-» ломал поиск, живой прогон
            // 2026-11-22; тот же паттерн, что SeedCaseGenericFamilies).
            var path = Path.Combine(directory, truth.Family + ".rfa");
            Document? familyDoc = null;
            try
            {
                familyDoc = Application.NewFamilyDocument(template);
                using (var transaction = new Transaction(familyDoc, "Seed case 14 family"))
                {
                    transaction.Start();
                    var manager = familyDoc.FamilyManager;
                    if (manager.CurrentType is not null)
                        manager.RenameCurrentType(truth.Type);
                    else
                        manager.NewType(truth.Type);

                    familyDoc.OwnerFamily.Name = truth.Family;
                    if (transaction.Commit() != TransactionStatus.Committed)
                    {
                        _case14SeedError = "Транзакция сида семейства «" + truth.Family +
                                           "» не завершилась";
                        return paths;
                    }
                }

                familyDoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
                Console.WriteLine("PROBE case14 family saved '" + path + "' bytes=" +
                                  (File.Exists(path) ? new FileInfo(path).Length : -1) +
                                  " ownerName='" + familyDoc.OwnerFamily.Name + "'");
                paths.Add(path);
            }
            catch (Exception ex)
            {
                _case14SeedError = "Не удалось создать семейство «" + truth.Family + "»: " +
                                   ex.GetType().Name + ": " + ex.Message;
                return paths;
            }
            finally
            {
                CloseCaseFamilyDoc(familyDoc);
            }
        }

        return paths;
    }

    /// <summary>
    /// Донор-эталон: уровни, 4 вида по паттерну, те же семейства с заполненной
    /// маркой (значения — второй транзакцией после привязки, механика кейса 13),
    /// SaveAs и Close. Транзакции в доноре НЕ открываются после сохранения.
    /// </summary>
    private void SeedCase14Donor(List<string> familyPaths, ExternalDefinition definition)
    {
        var donorPath = _case14DonorPath!;
        Document? donor = null;
        try
        {
            donor = Application.NewProjectDocument(UnitSystem.Metric);
            if (!ModelSeed.HasFloorPlanViewType(donor))
            {
                _case14SeedError = "В шаблоне нет ViewFamilyType для плана этажа (донор кейса 14)";
                return;
            }

            var transactions = new RevitTransactionService(new StubRevitContext(donor));
            transactions.RunInTransaction(donor, "Seed case 14 donor (bind)", doc =>
            {
                // Загрузка .rfa — ВНУТРИ транзакции: LoadFamily(string) без открытой
                // транзакции не бросает исключение, а молча возвращает false
                // (Autodesk forum 11565753; прецедент — титблоки Case13/SystemSheets).
                if (!Case14LoadFamilies(doc, familyPaths))
                    return;

                var level1 = ModelSeed.CreateLevel(doc, 0.0);
                level1.Name = Case14Level1;
                var level2 = ModelSeed.CreateLevel(doc, 3.0);
                level2.Name = Case14Level2;
                foreach (var viewName in Case14DonorViews)
                {
                    _ = ModelSeed.CreateViewPlanChecked(
                        doc, ContainsText(viewName, "Этаж 2") ? level2.Id : level1.Id, viewName);
                }

                Case14BindMark(doc, definition);
            });

            if (_case14SeedError is not null)
                return;

            transactions.RunInTransaction(donor, "Seed case 14 donor (marks)", doc =>
            {
                foreach (var truth in Case14FamilyTruth())
                {
                    var symbol = Case14FindSymbol(doc, truth.Family, truth.Type);
                    if (symbol is null)
                    {
                        _case14SeedError = "В доноре нет семейства «" +
                                           truth.Family + "/" + truth.Type + "»" +
                                           Case14DonorDiagnostics(doc);
                        return;
                    }

                    symbol.get_Parameter(definition)?.Set(truth.Mark);
                }
            });

            if (_case14SeedError is not null)
                return;

            donor.SaveAs(donorPath, new SaveAsOptions { OverwriteExistingFile = true });
        }
        catch (Exception ex)
        {
            _case14SeedError = "Донор кейса 14: " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            try
            {
                if (donor is not null && donor.IsValidObject)
                    donor.Close(false);
            }
            catch (Exception)
            {
                // Уже закрыт — ничего не делать.
            }
        }
    }

    private static void Case14BindMark(Document doc, ExternalDefinition definition)
    {
        var app = doc.Application;
        var category = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel);
        var categorySet = app.Create.NewCategorySet();
        categorySet.Insert(category);
        _ = doc.ParameterBindings.Insert(
            definition, app.Create.NewTypeBinding(categorySet), GroupTypeId.IdentityData);
    }

    private static FamilySymbol? Case14FindSymbol(
        Document doc, string familyName, string typeName)
    {
        foreach (var element in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_GenericModel)
                     .WhereElementIsElementType())
        {
            if (element is FamilySymbol symbol &&
                string.Equals(symbol.FamilyName, familyName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(symbol.Name, typeName, StringComparison.OrdinalIgnoreCase))
                return symbol;
        }

        return null;
    }

    private static Level? Case14FindLevel(Document doc, string levelName) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .FirstOrDefault(level => string.Equals(
                level.Name, levelName, StringComparison.Ordinal));

    private void VerifyCase14Seed()
    {
        if (_case14DonorPath is null || !File.Exists(_case14DonorPath) ||
            _case14DonorStamp is null || _case14DonorStamp.Length == 0)
        {
            Skip.Test("Сид кейса 14 некорректен: файл донора не сохранён");
            return;
        }

        if (_case14Views.Count != 4)
        {
            Skip.Test("Сид кейса 14 некорректен: видов основной модели " + _case14Views.Count);
            return;
        }

        foreach (var typeSeed in _case14Types)
        {
            if (!string.IsNullOrWhiteSpace(Case14MarkOf(typeSeed.TypeId)))
            {
                Skip.Test("Сид кейса 14 некорректен: марка «" + typeSeed.FamilyName +
                          "» основной модели не пуста");
                return;
            }
        }
    }

    private string? Case14MarkOf(long typeId) =>
        (Doc.GetElement(ElementIdCompat.Create(typeId)) as ElementType)
            ?.get_Parameter(Case14MarkGuid)?.AsString();

    private static string Case14LevelNameOf(View? view)
    {
        try
        {
            return view?.GenLevel?.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    // ── Чистка, сводка сида, дамп, тулы — в Case14.State.cs ───────────
    // Вердикт — в Case14.Evaluation.cs (EvaluateCase14).
}

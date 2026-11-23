using System.IO;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;

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
/// как классификатор в кейсе 10). Сид — в партиале Case14.Seed, вердикт — в
/// партиале Case14.Evaluation (EvaluateCase14).
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
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

    // ── Чтение истины по модели ─────────────────────────────────────────

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

    // ── Сид (файлы семейств, донор, основная модель, верификация) — в
    // Case14.Seed.cs; чистка, сводка сида, дамп, тулы — в Case14.State.cs;
    // вердикт — в Case14.Evaluation.cs (EvaluateCase14).
}

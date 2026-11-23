using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Agent.Tools.Jobs;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 9 of the real-cases benchmark = v2 case 4 (write, TWO-STEP): 3 clean
/// piping systems («Т1», «Т2», «Т1.11» — точка = ручное имя) and a suspicious
/// «Т11»/«Т111» pair split by a 7 mm gap (механика Case15). The shared
/// parameter «АДСК_ИмяСистемы» (project convention, given by the case) is
/// instance-bound to pipes/fittings/equipment/generic and filled PARTIALLY
/// (pipes of the clean systems only). The single 3D view template carries the
/// «Фильтры» Include-box CLEARED (VIS_GRAPHICS_FILTERS is non-controlled —
/// set_view_template_inclusion mechanics, done directly by the API here).
/// The model must first show the list of system names for approval (the
/// harness answers with a scripted user reply and keeps the intermediate
/// answer — the new RunCase scriptedUserReplies mechanics), then create one
/// 3D view per CLEAN system (name = system name, common template, filter
/// «АДСК_ИмяСистемы = имя»), fill the parameter for every member of the clean
/// systems via the set_parameters job (systemName + includeSubComponents) and
/// leave the suspicious pair without views, flagged as a probable break. The
/// seed truth, nested shared flanges (best-effort) and the state dump — in
/// Case9.Support; the verdict — in Case9.Evaluation (EvaluateCase9).
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // Дословно из SSOT (docs/agent/real-cases-benchmark-v2.md, кейс 4).
    private const string Case9Text =
        "В модели смоделированы трубопроводные системы. Мне нужно по 3D-виду на каждую " +
        "систему: вид показывает только элементы одной системы и называется её именем. " +
        "Сложность в том, что системное свойство «Имя системы» не достаётся вложенным " +
        "семействам — в проекте для этого заведён общий параметр «АДСК_ИмяСистемы», " +
        "который должен повторять имя системы у всех элементов, включая вложенные " +
        "общие семейства (фланцы и т.п.), иначе фильтр вида их не покажет. Сейчас он " +
        "заполнен не везде. Оформи каждый вид на базе нашего шаблона 3D-вида и фильтром " +
        "по этому общему параметру. Сначала разберись, какие имена систем есть в модели " +
        "и все ли названы руками: если увидишь похожие автодубликаты вроде «Т11» и " +
        "«Т111» — не делай на них виды, покажи мне их отдельно как подозрение на разрыв " +
        "системы, а список имён, по которым сделаю виды, утверди у меня.";

    private const string Case9Title =
        "3D-вид на каждую систему (фильтр по общему параметру имени системы)";

    private const string Case9ParamName = "АДСК_ИмяСистемы";
    private const string Case9SharedGroup = "АДСК";
    private const string Case9TemplateName = "3D Системы";
    private const string Case9HostFamilyName = "Насос с фланцем";
    private const string Case9SuspectNear = "Т11";
    private const string Case9SuspectFar = "Т111";
    private const double Case9GapFt = 0.023; // 7 мм — механика Case15/AgentRelationsToolsTests
    private const double Case9MepZ = -2.0;

    private static readonly Guid Case9ParamGuid = new("d7b2e4a1-6c35-4f8b-9e2d-1a4c8f3b5d09");

    private static readonly string[] Case9CleanSystems = { "Т1", "Т2", "Т1.11" };

    // Ни одного Revit-типа в полях (правило 1 интеграционных тестов).
    private sealed class Case9SystemSeed
    {
        public string Name { get; init; } = string.Empty;

        public readonly List<long> PipeIds = new();

        public readonly List<long> FittingIds = new();

        /// <summary>Члены системы post-commit (Elements + BaseEquipment + PipingNetwork).</summary>
        public readonly List<long> MemberIds = new();
    }

    private Dictionary<string, Case9SystemSeed> _case9Systems = null!;

    // Контекст двухшагового протокола последнего RunProductionCase-прогона
    // (C4-05): промежуточные ответы («утверди у меня») и число тул-вызовов,
    // исполненных ДО первого промежуточного ответа (-1 — шага не было).
    // Заполняется харнессом перед вызовом вердиктора.
    private IReadOnlyList<string> _lastRunIntermediateAnswers = Array.Empty<string>();

    private int _lastRunCallsBeforeFirstIntermediate = -1;

    /// <summary>Риски тулов каталога последнего RunProductionCase-прогона
    /// (для IsRealWrite в вердикторах, C4-05).</summary>
    private IReadOnlyDictionary<string, AgentToolRisk> _lastRunToolRisk =
        new Dictionary<string, AgentToolRisk>();

    private readonly List<long> _case9PumpIds = new();
    private readonly List<long> _case9FlangeIds = new();
    private readonly List<string> _case9TempFamilyPaths = new();
    private SharedParameterFileScope? _case9SharedFile;
    private AgentDatabase? _case9Database;
    private string? _case9DatabasePath;
    private SqliteAgentJobStore? _case9Store;
    private JobRunner? _case9Runner;
    private long _case9TemplateId;
    private long _case9ParamElementId;
    private long _case9HostSymbolId;
    private bool _case9FlangesSeeded;
    private string? _case9FlangeNote;
    private string? _case9SeedError;

    [Test]
    public async Task Case9_Live()
    {
        if (!LiveConfigured())
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        try
        {
            SeedOnBenchBaseLive(9, Case9Title, SeedCase9);
            var run = RunProductionCase(
                9, Case9Title, Case9Text,
                Case9SeedSummary(), EvaluateCase9, DumpCase9State,
                scriptedUserReplies: new[] { "Утверждаю: делай виды по этому списку" });

            // Мягкий ассерт: прогон вообще состоялся (есть ответ модели или вызовы).
            var runHappened = run.Calls.Count > 0 || !string.IsNullOrWhiteSpace(run.Answer);
            await Assert.That(runHappened).IsTrue();

            // Двухшаговый протокол кейса: список имён к утверждению = первый ответ
            // модели без тулов; харнесс отвечает согласием (scriptedUserReplies) и
            // сохраняет промежуточный ответ. Модель, проигнорировавшая шаг
            // утверждения («утверди у меня» в тексте кейса), валит ассерт сознательно.
            // C4-05: ассерт не должен выполняться любым текстом — промежуточный
            // ответ обязан содержать список имён систем (к утверждению).
            await Assert.That(run.IntermediateAnswers.Count).IsGreaterThanOrEqualTo(1);
            if (run.IntermediateAnswers.Count > 0)
            {
                var listed = Case9SystemsListed(run.IntermediateAnswers[0]);
                await Assert.That(listed).IsGreaterThanOrEqualTo(2);
            }
        }
        finally
        {
            CleanupCase9();
        }
    }

    /// <summary>Гейт сида для ночных прогонов без LLM: структура сида без live-гарда.</summary>
    [Test]
    public async Task Case9_SeedVerifies()
    {
        try
        {
            SeedOnBenchBase("case9", SeedCase9);

            foreach (var name in Case9AllSystemNames())
            {
                await Assert.That(_case9Systems.ContainsKey(name)).IsTrue();
                await Assert.That(_case9Systems[name].MemberIds.Count).IsGreaterThanOrEqualTo(2);
            }

            // Чистые системы — разные элементы (не слились в одну).
            var distinctSystems = Case9CleanSystems
                .Select(name => Case9FindSystem(name)?.Id.GetValue() ?? 0)
                .Distinct()
                .Count();
            await Assert.That(distinctSystems).IsEqualTo(Case9CleanSystems.Length);

            // Параметр существует и заполнен ЧАСТИЧНО: трубы чистых систем да, фитинги нет.
            await Assert.That(_case9ParamElementId).IsGreaterThan(0);
            var pipes = Case9CleanSystems.SelectMany(name => _case9Systems[name].PipeIds).ToList();
            var fittings = Case9CleanSystems.SelectMany(name => _case9Systems[name].FittingIds).ToList();
            await Assert.That(pipes.Count).IsGreaterThanOrEqualTo(3);
            await Assert.That(pipes.Count(id => !string.IsNullOrEmpty(Case9ParamValueOf(id))))
                .IsEqualTo(pipes.Count);
            await Assert.That(fittings.Count(id => !string.IsNullOrEmpty(Case9ParamValueOf(id))))
                .IsEqualTo(0);

            // Шаблон 3D один, секция «Фильтры» не управляется шаблоном.
            var template = Doc.GetElement(ElementIdCompat.Create(_case9TemplateId)) as View;
            await Assert.That(template is not null).IsTrue();
            await Assert.That(template!.IsTemplate).IsTrue();
            await Assert.That(template.ViewType).IsEqualTo(ViewType.ThreeD);
            await Assert.That(Case9TemplateFiltersDetached(template)).IsTrue();
            var threeDTemplates = new FilteredElementCollector(Doc)
                .OfClass(typeof(View)).Cast<View>()
                .Count(view => view.IsTemplate && view.ViewType == ViewType.ThreeD);
            await Assert.That(threeDTemplates).IsEqualTo(1);

            // C4-04: файловая загрузка хозяина в проект теперь в транзакции —
            // вложенные shared-фланцы обязаны засидиться (раньше LoadFamily
            // молча возвращал false, ключевой критерий кейса не проверялся).
            await Assert.That(_case9FlangesSeeded).IsTrue();
            await Assert.That(_case9FlangeIds.Count).IsGreaterThanOrEqualTo(2);
        }
        finally
        {
            CleanupCase9();
        }
    }

    // ── Сид ─────────────────────────────────────────────────────────────

    private void SeedCase9()
    {
        _document = SampleFiles.NewMepTemplateDocument(Application)
                    ?? Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasPipeTypes(Doc))
        {
            Skip.Test("В шаблоне проекта нет типов труб — сид кейса 9 невозможен");
            return;
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _case9Systems = new Dictionary<string, Case9SystemSeed>(StringComparer.Ordinal);
        _case9SeedError = null;

        _case9DatabasePath = Path.Combine(
            Path.GetTempPath(), "sc-case9bench-" + Guid.NewGuid().ToString("N") + ".db");
        _case9Database = new AgentDatabase(_case9DatabasePath);
        _case9Database.Initialize();
        _case9Store = new SqliteAgentJobStore(_case9Database);
        _case9Runner = new JobRunner(
            _case9Store, new IJobAction[] { new SetParametersJobAction() }, _transactions);
        // У тест-хоста нет ExternalEvent-насоса: get_job_status ведёт джобу дальше
        // на том же потоке (как обработчик product-насоса) — механика Case5.
        _case9Runner.Initialize(
            () => _case9Runner!.RunToCompletion(Doc, TimeSpan.FromMinutes(10)));

        // Вложенные фланцы строятся ВНЕ транзакции проекта (LoadFamily на
        // изменяемом документе падает — ловушка AgentFillAuditToolsTests).
        _case9FlangesSeeded = Case9TrySeedFlangeHosts(out _case9FlangeNote);

        _case9SharedFile = SharedParameterFileScope.Create(Application);
        var definition = Case9CreateSharedDefinition();
        if (_case9SeedError is not null)
        {
            Skip.Test(_case9SeedError);
            return;
        }

        _transactions.RunInTransaction(Doc, "Seed case 9 (systems, parameter, template)", doc =>
        {
            try
            {
                SeedCase9World(doc, definition!);
            }
            catch (Exception ex)
            {
                _case9SeedError = "Сид кейса 9: " + ex.GetType().Name + ": " + ex.Message;
            }
        });

        if (_case9SeedError is not null)
        {
            Skip.Test(_case9SeedError);
            return;
        }

        VerifyCase9Seed();
    }

    private Definition? Case9CreateSharedDefinition()
    {
        try
        {
            var file = Application.OpenSharedParameterFile();
            if (file is null)
            {
                _case9SeedError = "Временный ФОП не открылся";
                return null;
            }

            var group = file.Groups.get_Item(Case9SharedGroup) ?? file.Groups.Create(Case9SharedGroup);
            return group!.Definitions.Create(
                new ExternalDefinitionCreationOptions(Case9ParamName, SpecTypeId.String.Text)
                {
                    GUID = Case9ParamGuid
                });
        }
        catch (Exception ex)
        {
            _case9SeedError = "Не удалось создать общий параметр «" + Case9ParamName + "»: " +
                              ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private void SeedCase9World(Document doc, Definition definition)
    {
        // (1) Приёмник: общий параметр, instance-binding на трубах/фитингах/
        // оборудовании/generic (фланцы-вложенники — generic model).
        var categories = Application.Create.NewCategorySet();
        categories.Insert(Category.GetCategory(doc, BuiltInCategory.OST_PipeCurves));
        categories.Insert(Category.GetCategory(doc, BuiltInCategory.OST_PipeFitting));
        categories.Insert(Category.GetCategory(doc, BuiltInCategory.OST_MechanicalEquipment));
        categories.Insert(Category.GetCategory(doc, BuiltInCategory.OST_GenericModel));
        doc.ParameterBindings.Insert(
            definition, Application.Create.NewInstanceBinding(categories), GroupTypeId.Text);

        var sharedElement = SharedParameterElement.Lookup(doc, Case9ParamGuid);
        _case9ParamElementId = sharedElement?.Id.GetValue() ?? 0;

        var level = ModelSeed.CreateLevel(doc, 0.0);
        level.Name = "Этаж 1";

        // (2) Ветви. Трубы разных ветвей — разных PipingSystemType (механика
        // AgentRelationsToolsTests: физически раздельные сети не сливаются,
        // разные типы — дополнительная страховка от автосклейки имён).
        // Классификация Other/Undefined логических систем не образует — фильтруем.
        var systemTypes = new FilteredElementCollector(doc)
            .OfClass(typeof(PipingSystemType))
            .Cast<PipingSystemType>()
            .Where(systemType => systemType.SystemClassification.ToString()
                is not "Other" and not "Undefined")
            .Select(systemType => systemType.Id)
            .ToList();
        var pipeTypeId = BenchFirstTypeId(doc, typeof(PipeType));
        if (systemTypes.Count == 0 || pipeTypeId is null)
        {
            _case9SeedError = "PipeType/PipingSystemType исчезли после проверки шаблона";
            return;
        }

        Case9SeedCleanBranch(doc, systemTypes, pipeTypeId, level.Id, 0, 0, "Т1");
        Case9SeedCleanBranch(doc, systemTypes, pipeTypeId, level.Id, 1, 30, "Т2");
        Case9SeedCleanBranch(doc, systemTypes, pipeTypeId, level.Id, 2, 60, "Т1.11");
        Case9SeedSuspiciousPair(doc, systemTypes, pipeTypeId, level.Id, 3, 90);
        if (_case9SeedError is not null)
            return;

        doc.Regenerate();

        // (3) Частичное заполнение: трубы чистых систем, фитинги пусты.
        foreach (var systemName in Case9CleanSystems)
        {
            foreach (var pipeId in _case9Systems[systemName].PipeIds)
            {
                doc.GetElement(ElementIdCompat.Create(pipeId))
                    ?.get_Parameter(Case9ParamGuid)?.Set(systemName);
            }
        }

        // (4) Шаблон 3D-вида: один, «Фильтры» не управляются шаблоном.
        Case9Seed3DTemplate(doc);
        if (_case9SeedError is not null)
            return;

        // (5) Насосы с вложенными shared-фланцами (если собрались).
        Case9PlacePumps(doc);
    }

    private void Case9SeedCleanBranch(
        Document doc,
        IReadOnlyList<ElementId> systemTypes,
        ElementId pipeTypeId,
        ElementId levelId,
        int typeIndex,
        double offsetX,
        string systemName)
    {
        var seed = new Case9SystemSeed { Name = systemName };
        _case9Systems[systemName] = seed;

        var systemTypeId = systemTypes[typeIndex % systemTypes.Count];
        var start = new XYZ(offsetX, 0, Case9MepZ);
        var corner = new XYZ(offsetX + 10, 0, Case9MepZ);
        var end = new XYZ(offsetX + 10, 10, Case9MepZ);
        var pipe1 = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, start, corner);
        var pipe2 = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, corner, end);
        if (pipe1 is null || pipe2 is null)
        {
            _case9SeedError = "Трубы системы «" + systemName + "» не создались";
            return;
        }

        doc.Regenerate();
        var elbow = doc.Create.NewElbowFitting(
            ModelSeed.FindConnectorAt(pipe1, corner),
            ModelSeed.FindConnectorAt(pipe2, corner));
        doc.Regenerate();

        // Pipe.Create отдаёт коннекторы автосистеме: ветвь соединена — одна
        // логическая система; переименование автосистемы (механика
        // AgentSystemFillJobTests, ручной PipingSystem.Create не работает).
        if (pipe1.MEPSystem is not PipingSystem system)
        {
            _case9SeedError = "Автосистема не создалась у ветви «" + systemName + "»";
            return;
        }

        Case9Rename(system, systemName);
        doc.Regenerate();

        seed.PipeIds.Add(pipe1.Id.GetValue());
        seed.PipeIds.Add(pipe2.Id.GetValue());
        seed.FittingIds.Add(elbow.Id.GetValue());
    }

    /// <summary>
    /// Подозрительная пара: соединённая ветвь (2 трубы + колено) в «Т11» и
    /// дальняя сторона за зазором 7 мм (2 коллинеарные трубы, ConnectTo) в
    /// «Т111» — физически один контур, Revit видит две системы (механика
    /// Case15/AgentRelationsToolsTests).
    /// </summary>
    private void Case9SeedSuspiciousPair(
        Document doc,
        IReadOnlyList<ElementId> systemTypes,
        ElementId pipeTypeId,
        ElementId levelId,
        int typeIndex,
        double offsetX)
    {
        var near = new Case9SystemSeed { Name = Case9SuspectNear };
        var far = new Case9SystemSeed { Name = Case9SuspectFar };
        _case9Systems[Case9SuspectNear] = near;
        _case9Systems[Case9SuspectFar] = far;

        var nearType = systemTypes[typeIndex % systemTypes.Count];
        var farType = systemTypes[(typeIndex + 1) % systemTypes.Count];
        var z = Case9MepZ;
        var start = new XYZ(offsetX, 0, z);
        var corner = new XYZ(offsetX + 10, 0, z);
        var bend = new XYZ(offsetX + 10, 10, z);
        var pipe1 = Pipe.Create(doc, nearType, pipeTypeId, levelId, start, corner);
        var pipe2 = Pipe.Create(doc, nearType, pipeTypeId, levelId, corner, bend);
        if (pipe1 is null || pipe2 is null)
        {
            _case9SeedError = "Трубы ветви «" + Case9SuspectNear + "» не создались";
            return;
        }

        doc.Regenerate();
        var elbow = doc.Create.NewElbowFitting(
            ModelSeed.FindConnectorAt(pipe1, corner),
            ModelSeed.FindConnectorAt(pipe2, corner));
        doc.Regenerate();
        if (pipe1.MEPSystem is not PipingSystem nearSystem)
        {
            _case9SeedError = "Автосистема не создалась у ветви «" + Case9SuspectNear + "»";
            return;
        }

        Case9Rename(nearSystem, Case9SuspectNear);
        near.PipeIds.Add(pipe1.Id.GetValue());
        near.PipeIds.Add(pipe2.Id.GetValue());
        near.FittingIds.Add(elbow.Id.GetValue());

        var gapEnd = new XYZ(offsetX + 10, 10 + Case9GapFt, z);
        var joint = new XYZ(offsetX + 10, 18 + Case9GapFt, z);
        var end = new XYZ(offsetX + 10, 26 + Case9GapFt, z);
        var pipe3 = Pipe.Create(doc, farType, pipeTypeId, levelId, gapEnd, joint);
        var pipe4 = Pipe.Create(doc, farType, pipeTypeId, levelId, joint, end);
        if (pipe3 is null || pipe4 is null)
        {
            _case9SeedError = "Трубы ветви «" + Case9SuspectFar + "» не создались";
            return;
        }

        doc.Regenerate();
        ModelSeed.FindConnectorAt(pipe3, joint).ConnectTo(ModelSeed.FindConnectorAt(pipe4, joint));
        doc.Regenerate();
        if (pipe3.MEPSystem is not PipingSystem farSystem || farSystem.Id.Equals(nearSystem.Id))
        {
            _case9SeedError = "Стороны зазора не разделились в две системы";
            return;
        }

        Case9Rename(farSystem, Case9SuspectFar);
        doc.Regenerate();
        far.PipeIds.Add(pipe3.Id.GetValue());
        far.PipeIds.Add(pipe4.Id.GetValue());
    }

    private void Case9Seed3DTemplate(Document doc)
    {
        // Чужие 3D-шаблоны шаблона проекта удаляем: кейс требует ОДИН шаблон,
        // иначе модель может выбрать не наш.
        var familyType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(type => type.ViewFamily == ViewFamily.ThreeDimensional);
        if (familyType is null)
        {
            _case9SeedError = "Нет ViewFamilyType для 3D-вида";
            return;
        }

        var source = View3D.CreateIsometric(doc, familyType.Id);
        var template = source.CreateViewTemplate();
        if (template is null)
        {
            _case9SeedError = "CreateViewTemplate вернул null для 3D-вида";
            return;
        }

        template.Name = Case9TemplateName;

        // Include-галочка «Фильтры» СНЯТА (механика SetViewTemplateInclusionTool):
        // отсоединённые секции живут в NonControlledTemplateParameterIds —
        // фильтры остаются на видах, один шаблон обслуживает разные фильтры.
        var filtersValue = new ElementId(BuiltInParameter.VIS_GRAPHICS_FILTERS).GetValue();
        var controlled = template.GetTemplateParameterIds().ToList();
        if (controlled.All(id => id.GetValue() != filtersValue))
        {
            _case9SeedError = "VIS_GRAPHICS_FILTERS отсутствует в параметрах 3D-шаблона";
            return;
        }

        var nonControlled = template.GetNonControlledTemplateParameterIds().ToList();
        if (nonControlled.All(id => id.GetValue() != filtersValue))
            nonControlled.Add(new ElementId(BuiltInParameter.VIS_GRAPHICS_FILTERS));
        template.SetNonControlledTemplateParameterIds(nonControlled);

        _case9TemplateId = template.Id.GetValue();
        doc.Delete(source.Id);

        foreach (var other in new FilteredElementCollector(doc)
                     .OfClass(typeof(View)).Cast<View>()
                     .Where(view => view.IsTemplate && view.ViewType == ViewType.ThreeD &&
                                    view.Id.GetValue() != _case9TemplateId).ToList())
        {
            try
            {
                doc.Delete(other.Id);
            }
            catch (Exception)
            {
                // Нельзя удалить — верификатор сида увидит лишний шаблон.
            }
        }
    }

    // ── Сводка сида и тулы ──────────────────────────────────────────────

    private List<string> Case9SeedSummary()
    {
        var lines = new List<string>
        {
            "MEP-шаблон, уровень «Этаж 1»; 5 трубопроводных систем (ветви разных PipingSystemType):",
            "- чистые (ручные имена): «Т1», «Т2», «Т1.11» (точка = ручное именование)",
            "- подозрительная пара: «" + Case9SuspectNear + "»/«" + Case9SuspectFar +
            "» — зазор 7 мм, физически один контур (вероятный разрыв системы)",
            "каждая ветвь: 2 трубы + колено; общий параметр «" + Case9ParamName +
            "» (shared, instance: трубы/фитинги/оборудование/generic) заполнен частично — " +
            "трубы чистых систем заполнены, фитинги и оборудование пусты",
            "шаблон 3D-вида один «" + Case9TemplateName + "\" (id " + _case9TemplateId +
            "), Include-галочка «Фильтры» снята — фильтры живут на видах"
        };
        foreach (var name in Case9AllSystemNames())
            lines.Add("  - «" + name + "»: труб " + _case9Systems[name].PipeIds.Count +
                      ", фитингов " + _case9Systems[name].FittingIds.Count +
                      ", членов " + _case9Systems[name].MemberIds.Count);
        lines.Add(_case9FlangesSeeded
            ? "2 насоса (Н1/Н2) с вложенными shared-фланцами (id " +
              string.Join(", ", _case9FlangeIds) + "), параметр у фланцев пуст"
            : "вложенные фланцы не собрались (кейс деградирует честно): " +
              (_case9FlangeNote ?? "причина неизвестна"));
        return lines;
    }


    private void CleanupCase9()
    {
        _case9SharedFile?.Dispose();
        _case9SharedFile = null;
        _case9Database = null;
        _case9Store = null;
        _case9Runner = null;
        try
        {
            if (_case9DatabasePath is not null && File.Exists(_case9DatabasePath))
                File.Delete(_case9DatabasePath);
        }
        catch (IOException)
        {
            // Файл может быть занят — очистка best-effort.
        }

        foreach (var path in _case9TempFamilyPaths)
            Case9TryDeleteFile(path);
        _case9TempFamilyPaths.Clear();
    }
}

using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SmartCon.Agent.Services.Decisions;
using SmartCon.Agent.Services.Storage;
using SmartCon.Agent.Services.Tools.Semantic;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Jobs;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Seed and tool wiring of benchmark case 7 (v2 case 2): ФОП подменяется
/// временным файлом (целевая группа «ФОП Заказчика» + 4 «чужих» исходных
/// набора), целевые определения биндингом на категорию generic (type), старые
/// параметры добавляются В СЕМЕЙСТВА через EditFamily (12 сессий — по одной
/// на семейство, см. COST NOTE в Case7.cs), затем размещение 10 экземпляров,
/// листовая ведомость на титульном листе с видами-полировкой и приманка вне
/// листов. Джоба map_family_parameters (createTarget) доступна через
/// start_job. Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private AgentDatabase? _case7Database;

    private SqliteAgentJobStore? _case7Store;

    private JobRunner? _case7Runner;

    private IJobAction[]? _case7JobActions;

    private HttpDecisionClient? _case7Decisions;

    private string? _case7DatabasePath;

    private string? _case7SeedError;

    // ── Сид ─────────────────────────────────────────────────────────────

    private void SeedCase7()
    {
        _document = BenchDocuments.NewBase(Application);
        _case7SeedError = null;
        _case7ViewsOnSheet = false;
        _case7Truth = Case7Truth();

        _case7SharedFile = SharedParameterFileScope.Create(Application);

        // Семейства грузятся ВНЕ транзакции (LoadFamily на изменяемом документе падает).
        var spec = _case7Truth
            .GroupBy(row => row.Family, StringComparer.OrdinalIgnoreCase)
            .Select(group => (
                group.Key,
                group.Select(row => row.Type).ToArray()))
            .ToArray();
        var families = SeedCaseGenericFamilies(spec, out var skipReason);
        if (families is null)
        {
            Skip.Test(skipReason ?? "Сид-семейства кейса 7 не собрались");
            return;
        }

        // Определения ФОП создаются ПОСЛЕ загрузки семейств: перезапись тёзок
        // шаблона владельца заставляет Revit перечитывать ФОП, и обёртки,
        // полученные раньше, протухают (InvalidObjectExceptionProxy в Insert).
        var definitions = SeedCase7Definitions();
        if (_case7SeedError is not null)
        {
            Skip.Test(_case7SeedError);
            return;
        }

        _case7Families = families;
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        SeedCase7JobInfrastructure();

        // (1) Целевые определения — биндинг на категорию (type): поля листовой
        // ведомости и приёмник значений материализуются только при коммите.
        _transactions.RunInTransaction(Doc, "Seed case 7 (bind)", doc =>
            BindCase7Targets(doc, definitions!));
        if (_case7SeedError is not null)
        {
            Skip.Test(_case7SeedError);
            return;
        }

        // (2) Старые наборы — ВНУТРЬ семейств через EditFamily (вне транзакций
        // проекта): общий параметр семейства с GUID своего «чужого ФОПа».
        SeedCase7FamilyParameters();
        if (_case7SeedError is not null)
        {
            Skip.Test(_case7SeedError);
            return;
        }

        RefreshCase7TypeIds();
        if (_case7SeedError is not null)
        {
            Skip.Test(_case7SeedError);
            return;
        }

        // (3) Размещение + отчётность: ведомость на листе, приманка вне листов.
        _transactions.RunInTransaction(Doc, "Seed case 7 (model)", doc =>
        {
            PlaceCase7Instances(doc);
            SeedCase7Reporting(doc);
        });
        if (_case7SeedError is not null)
        {
            Skip.Test(_case7SeedError);
            return;
        }

        VerifyCase7Seed();
    }

    private void SeedCase7JobInfrastructure()
    {
        _case7DatabasePath = Path.Combine(
            Path.GetTempPath(), "sc-case7bench-" + Guid.NewGuid().ToString("N") + ".db");
        _case7Database = new AgentDatabase(_case7DatabasePath);
        _case7Database.Initialize();
        _case7Store = new SqliteAgentJobStore(_case7Database);
        _case7JobActions = new IJobAction[]
        {
            new MapFamilyParametersJobAction(),
            new ReplaceFamilyParameterJobAction(),
            new RenameViewsJobAction(),
            new SetParametersJobAction()
        };
        _case7Runner = new JobRunner(_case7Store, _case7JobActions, _transactions!);
        // The test host has no ExternalEvent pump: get_job_status drives the job
        // forward on the same thread, mirroring the production pump handler.
        _case7Runner.Initialize(() => _case7Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(10)));
    }

    private Case7Definitions? SeedCase7Definitions()
    {
        // SEED-8 (тот же нативный сбой ФОП, что у кейса 8): определения
        // создаются устойчивым хелпером с форс-перечитыванием ФОП и ретраем
        // по причине (см. EnsureCaseSharedDefinition).
        var targets = new ExternalDefinition[Case7RoleCount];
        for (var role = 0; role < Case7RoleCount; role++)
        {
            var (definition, error) = EnsureCaseSharedDefinition(
                Case7TargetGroupName, Case7TargetNames[role], Case7TargetGuids[role]);
            if (error is not null)
            {
                _case7SeedError = "Не удалось засидировать определения ФОП кейса 7: " + error;
                return null;
            }

            targets[role] = definition!;
        }

        var old = new ExternalDefinition?[4][];
        for (var group = 0; group < 4; group++)
        {
            old[group] = new ExternalDefinition?[Case7RoleCount];
            for (var role = 0; role < Case7RoleCount; role++)
            {
                var name = Case7OldNames[group][role];
                var guid = Case7OldGuids[group][role];
                if (name is null || guid is null)
                    continue;

                var (definition, error) = EnsureCaseSharedDefinition(
                    Case7OldGroupNames[group], name, guid.Value);
                if (error is not null)
                {
                    _case7SeedError = "Не удалось засидировать определения ФОП кейса 7: " + error;
                    return null;
                }

                old[group][role] = definition;
            }
        }

        return new Case7Definitions(targets, old);
    }

    private void BindCase7Targets(Document doc, Case7Definitions definitions)
    {
        try
        {
            var app = doc.Application;
            var category = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel);
            var categorySet = app.Create.NewCategorySet();
            categorySet.Insert(category);
            var typeBinding = app.Create.NewTypeBinding(categorySet);
            for (var role = 0; role < Case7RoleCount; role++)
            {
                // Обёртка резолвится в момент КАЖДОГО Insert: managed-IsValidObject
                // внешних определений врёт для нативно протухших — см. док-комментарий
                // FindCaseSharedDefinition.
                var definition = FindCaseSharedDefinition(
                    Case7TargetGroupName, Case7TargetNames[role]);
                if (definition is null)
                {
                    _case7SeedError = "Целевой параметр «" + Case7TargetNames[role] +
                                       "» не найден в ФОП перед привязкой (обёртка протухла)";
                    return;
                }

                if (!doc.ParameterBindings.Insert(definition, typeBinding))
                {
                    _case7SeedError = "Не удалось привязать целевой параметр «" +
                                       definition.Name + "» к категории";
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _case7SeedError = "Привязка целевых параметров кейса 7: " +
                               ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// Добавляет старые общие параметры в семейства через EditFamily
    /// (паттерн AgentMigrationDogfoodTests.SeedOldMarkParameter): одна сессия
    /// редактора на семейство, одна транзакция со всеми его параметрами,
    /// значения — по CurrentType, LoadFamily обратно с перезаписью.
    /// ExternalDefinition-объекты НЕ держатся между сессиями: после первой
    /// overwrite-загрузки семейства, несущего shared-параметры, Revit
    /// перечитывает файл общих параметров и нативные определения умирают
    /// (InvalidObjectExceptionProxy в ExternalDefinition.getParamTypeSpec,
    /// точечный прогон 2026-11-22), поэтому определения ре-резолвятся из ФОП
    /// по имени перед КАЖДОЙ сессией.
    /// </summary>
    private void SeedCase7FamilyParameters()
    {
        foreach (var row in _case7Truth)
        {
            var family = FindCase7ProjectFamily(row.Family);
            if (family is null)
            {
                _case7SeedError = "Сид-семейство «" + row.Family + "» не найдено после загрузки";
                return;
            }

            Document? familyDoc = null;
            try
            {
                familyDoc = Doc.EditFamily(family);
                var manager = familyDoc.FamilyManager;
                using (var transaction = new Transaction(
                           familyDoc, "Seed case 7 old parameters"))
                {
                    transaction.Start();
                    var parameters = new FamilyParameter?[Case7RoleCount];
                    for (var role = 0; role < Case7RoleCount; role++)
                    {
                        var definition = Case7FreshOldDefinition(familyDoc, row.Group, role);
                        if (Case7OldNames[row.Group][role] is not null && definition is null)
                        {
                            _case7SeedError = "Определение «" + Case7OldNames[row.Group][role] +
                                               "» не найдено в ФОП (протухло после перечитывания)";
                            return;
                        }

                        if (definition is not null)
                            parameters[role] = manager.AddParameter(
                                definition, GroupTypeId.General, false);
                    }

                    foreach (FamilyType type in manager.Types)
                    {
                        manager.CurrentType = type;
                        for (var role = 0; role < Case7RoleCount; role++)
                        {
                            var value = row.Old[role];
                            // Пустая строка = параметр есть, значения нет (инвалид
                            // «значение пустое») — Set не вызываем.
                            if (parameters[role] is not null && !string.IsNullOrEmpty(value))
                                manager.Set(parameters[role], value);
                        }
                    }

                    if (transaction.Commit() != TransactionStatus.Committed)
                    {
                        _case7SeedError = "Не удалось записать старые параметры в «" +
                                           row.Family + "»";
                        return;
                    }
                }

                familyDoc.LoadFamily(Doc, new OverwriteFamilyLoadOptions());
                // Bare Doc.Regenerate() is NOT legal outside a transaction (Revit
                // treats it as a modification, point run 2026-11-22); the graph
                // refresh lives in an empty transaction so no stale Family proxy
                // leaks into the next session's collector resolve.
                using (var refresh = new Transaction(Doc, "Seed case 7 refresh"))
                {
                    refresh.Start();
                    Doc.Regenerate();
                    if (refresh.Commit() != TransactionStatus.Committed)
                    {
                        _case7SeedError = "Регенерация после загрузки «" +
                                           row.Family + "» не завершилась";
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _case7SeedError = "Сид старых параметров «" + row.Family + "»: " +
                                   ex.GetType().Name + ": " + ex.Message;
                return;
            }
            finally
            {
                if (familyDoc is not null && familyDoc.IsValidObject)
                    familyDoc.Close(false);
            }
        }
    }

    /// <summary>
    /// Свежий ExternalDefinition старого параметра из текущего ФОП (по имени
    /// группы и параметра). Обёртки, полученные при создании определений,
    /// протухают нативно после первой загрузки семейства с shared-параметрами
    /// — см. док-комментарий SeedCase7FamilyParameters.
    /// </summary>
    private static ExternalDefinition? Case7FreshOldDefinition(
        Document familyDoc, int group, int role)
    {
        var oldName = Case7OldNames[group][role];
        if (oldName is null)
            return null;

        try
        {
            var file = familyDoc.Application.OpenSharedParameterFile();
            if (file is null)
                return null;

            foreach (DefinitionGroup candidate in file.Groups)
            {
                if (!string.Equals(
                        candidate.Name, Case7OldGroupNames[group], StringComparison.Ordinal))
                    continue;

                foreach (Definition definition in candidate.Definitions)
                    if (string.Equals(definition.Name, oldName, StringComparison.Ordinal) &&
                        definition is ExternalDefinition external)
                        return external;
            }
        }
        catch (Exception)
        {
            // ФОП недоступен — вызывающий превратит null в ошибку сида.
        }

        return null;
    }

    /// <summary>Перечитывает id типоразмеров после LoadFamily-перезаписи.</summary>
    private void RefreshCase7TypeIds()
    {
        foreach (var row in _case7Truth)
        {
            var family = FindCase7ProjectFamily(row.Family);
            var symbol = family is null ? null : FindCaseFamilySymbol(family, row.Type);
            if (symbol is null)
            {
                _case7SeedError = "Тип «" + row.Type + "» семейства «" + row.Family +
                                   "» не найден после EditFamily";
                return;
            }

            row.TypeId = symbol.Id.GetValue();
        }
    }

    private void PlaceCase7Instances(Document doc)
    {
        // Размещаются только строки истины с Placed=true (10 из 12).
        var placed = 0;
        foreach (var row in _case7Truth.Where(row => row.Placed))
        {
            if (doc.GetElement(ElementIdCompat.Create(row.TypeId)) is not FamilySymbol symbol)
                continue;

            symbol.Activate();
            var instance = doc.Create.NewFamilyInstance(
                new XYZ((placed % 5) * 5.0, (placed / 5) * 5.0, 0.0),
                symbol,
                StructuralType.NonStructural);
            row.InstanceId = instance.Id.GetValue();
            placed++;
        }
    }

    private void SeedCase7Reporting(Document doc)
    {
        var sheetSchedule = ViewSchedule.CreateSchedule(
            doc, new ElementId(BuiltInCategory.OST_GenericModel));
        sheetSchedule.Name = Case7SheetScheduleName;
        var definition = sheetSchedule.Definition;
        foreach (var guid in Case7TargetGuids)
        {
            if (!TryAddCaseSharedField(doc, definition, guid))
            {
                _case7SeedError = "Поле целевого параметра (guid …" +
                                   guid.ToString("D").Substring(guid.ToString("D").Length - 6) +
                                   ") не найдено среди schedulable — листовая ведомость не собрана";
                return;
            }
        }

        var combined = FindCaseSchedulableFieldByName(
            doc, definition, "Семейство и типоразмер", "Family and Type");
        combined ??= FindCaseSchedulableFieldByName(doc, definition, "Семейство", "Family");
        if (combined is not null)
            definition.AddField(combined);

        // Приманка: старые поля группы А, НЕ на листе.
        var bait = ViewSchedule.CreateSchedule(
            doc, new ElementId(BuiltInCategory.OST_GenericModel));
        bait.Name = Case7BaitScheduleName;
        for (var role = 0; role < Case7RoleCount; role++)
        {
            var guid = Case7OldGuids[0][role];
            if (guid is not null && !TryAddCaseSharedField(doc, bait.Definition, guid.Value))
            {
                _case7SeedError = "Поле приманки «" + Case7OldNames[0][role] +
                                   "» не найдено среди schedulable";
                return;
            }
        }

        var titleBlockId = ModelSeed.FindTitleBlockTypeId(doc);
        if (titleBlockId is null)
        {
            var titleBlockPath = ModelSeed.FindTitleBlockFamilyFile(doc.Application.VersionNumber);
            if (titleBlockPath is not null)
            {
                doc.LoadFamily(titleBlockPath, out _);
                titleBlockId = ModelSeed.FindTitleBlockTypeId(doc);
            }
        }

        if (titleBlockId is null)
        {
            _case7SeedError = "В шаблоне нет Title Block — листовая ведомость невозможна";
            return;
        }

        var sheet = ViewSheet.Create(doc, titleBlockId);
        sheet.SheetNumber = Case7SheetNumber;
        sheet.Name = "Ведомость объёмов работ (лист)";

        // Сигнал «модель в полировке»: пара видов на листе. Viewport.Create
        // возвращает null без регенерации геометрии — стена на уровне вида.
        if (ModelSeed.HasFloorPlanViewType(doc))
        {
            try
            {
                var level = ModelSeed.CreateLevel(doc, 0.0);
                var wall = ModelSeed.TryCreateWallAt(doc, level.Id, 0.0);
                if (wall is not null)
                {
                    var plan = ModelSeed.CreateViewPlanChecked(doc, level.Id, "План размещения");
                    doc.Regenerate();
                    _case7ViewsOnSheet =
                        Viewport.Create(doc, sheet.Id, plan.Id, new XYZ(0.2, 0.3, 0)) is not null;
                }
            }
            catch (Exception)
            {
                _case7ViewsOnSheet = false;
            }
        }

        ScheduleSheetInstance.Create(doc, sheet.Id, sheetSchedule.Id, new XYZ(0.08, 0.08, 0));
        _case7SheetScheduleId = sheetSchedule.Id.GetValue();
        _case7BaitScheduleId = bait.Id.GetValue();
        _case7SheetCreated = true;
    }

    private void VerifyCase7Seed()
    {
        if (!_case7SheetCreated)
        {
            Skip.Test("Листовая ведомость кейса 7 не создана");
            return;
        }

        var placed = _case7Truth.Count(row => row.Placed && row.InstanceId != 0);
        var unplaced = _case7Truth.Count(row => !row.Placed && row.InstanceId == 0);
        if (placed != 10 || unplaced != 2)
        {
            Skip.Test("Сид кейса 7 некорректен: размещено " + placed + ", не размещено " +
                      unplaced + " (нужно 10/2)");
            return;
        }

        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in _case7Truth)
            signatures.Add(Case7SignatureOf(row));
        if (signatures.Count != 4)
        {
            Skip.Test("Сид кейса 7 некорректен: сигнатур исходных наборов " + signatures.Count);
        }
    }

    private RevitFamily? FindCase7ProjectFamily(string familyName)
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(RevitFamily)))
        {
            if (element is RevitFamily family &&
                string.Equals(family.Name, familyName, StringComparison.OrdinalIgnoreCase))
                return family;
        }

        return null;
    }

    // ── Тулы ────────────────────────────────────────────────────────────


    private void CleanupCase7()
    {
        DisposeCaseGenericFamilies(_case7Families);
        _case7SharedFile?.Dispose();
        _case7Decisions?.Dispose();
        _case7Decisions = null;
        _case7Database = null;
        _case7Store = null;
        _case7Runner = null;
        try
        {
            if (_case7DatabasePath is not null && File.Exists(_case7DatabasePath))
                File.Delete(_case7DatabasePath);
        }
        catch (IOException)
        {
            // Temp file cleanup is best-effort.
        }
    }
}

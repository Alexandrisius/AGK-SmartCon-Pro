using Autodesk.Revit.DB;
using SmartCon.Agent.Services.Decisions;
using SmartCon.Agent.Services.Tools.Semantic;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Seed and tool wiring of benchmark case 8 (v2 case 3): ФОП подменяется
/// временным файлом (целевая группа «ФОП Заказчика» + старая «АДСК»), ОБА
/// набора биндятся на категорию generic (type) и заполняются на типоразмерах
/// («миграция кейса 2 уже прошла»), затем собираются три ГОСТ-спеки (большая —
/// 9 колонок со скрытым столбцом, сортировкой 2 уровня, фильтром, итогами и
/// полосатыми строками; средняя и простая — на общем листе) и приманка вне
/// листов. Колонка «А_Примечание» не имеет целевого аналога — ожидаем её
/// пропуск в новой спеке. Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private HttpDecisionClient? _case8Decisions;

    private string? _case8SeedError;

    // ── Сид ─────────────────────────────────────────────────────────────

    private void SeedCase8()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _case8SeedError = null;
        _case8Truth = Case8Truth();
        _case8Placements = new List<Case8Placement>();

        _case8SharedFile = SharedParameterFileScope.Create(Application);
        var definitions = SeedCase8Definitions();
        if (_case8SeedError is not null)
        {
            Skip.Test(_case8SeedError);
            return;
        }

        // Семейства грузятся ВНЕ транзакции (LoadFamily на изменяемом документе падает).
        var spec = _case8Truth
            .GroupBy(row => row.Family, StringComparer.OrdinalIgnoreCase)
            .Select(group => (
                group.Key,
                group.Select(row => row.Type).ToArray()))
            .ToArray();
        var families = SeedCaseGenericFamilies(spec, out var skipReason);
        if (families is null)
        {
            Skip.Test(skipReason ?? "Сид-семейства кейса 8 не собрались");
            return;
        }

        _case8Families = families;
        foreach (var seed in _case8Families)
        {
            foreach (var type in seed.Types)
            {
                var row = _case8Truth.Single(item =>
                    string.Equals(item.Family, seed.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Type, type.TypeName, StringComparison.OrdinalIgnoreCase));
                row.TypeId = type.TypeId;
            }
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        // (1) Биндинг обоих наборов на категорию (type): поля спек материализуются
        // только при коммите (урок кейса 13).
        _transactions.RunInTransaction(Doc, "Seed case 8 (bind)", doc =>
            BindCase8Parameters(doc, definitions!));
        if (_case8SeedError is not null)
        {
            Skip.Test(_case8SeedError);
            return;
        }

        // (2) Размещение, значения обоих наборов, спеки и листы.
        _transactions.RunInTransaction(Doc, "Seed case 8 (model)", doc =>
        {
            PlaceCase8Instances(doc);
            SeedCase8Values(doc, definitions!);
            if (_case8SeedError is not null)
                return;

            SeedCase8Schedules(doc);
        });
        if (_case8SeedError is not null)
        {
            Skip.Test(_case8SeedError);
            return;
        }

        VerifyCase8Seed();
    }

    private Case8Definitions? SeedCase8Definitions()
    {
        try
        {
            var file = Application.OpenSharedParameterFile();
            if (file is null)
            {
                _case8SeedError = "Временный ФОП кейса 8 не открылся";
                return null;
            }

            // Groups.Create throws an empty InvalidOperationException when the
            // group already exists: create each group once and reuse it.
            var groupsByName = new Dictionary<string, DefinitionGroup>(
                StringComparer.OrdinalIgnoreCase);
            DefinitionGroup GroupOf(string groupName)
            {
                if (groupsByName.TryGetValue(groupName, out var existing))
                    return existing;

                var created = file.Groups.Create(groupName);
                groupsByName[groupName] = created;
                return created;
            }

            ExternalDefinition Create(string groupName, string name, Guid guid)
            {
                var group = GroupOf(groupName);
                return (ExternalDefinition)group.Definitions.Create(
                    new ExternalDefinitionCreationOptions(name, SpecTypeId.String.Text)
                    {
                        GUID = guid
                    });
            }

            var targets = new ExternalDefinition[Case7RoleCount];
            for (var role = 0; role < Case7RoleCount; role++)
                targets[role] = Create(
                    Case7TargetGroupName, Case7TargetNames[role], Case7TargetGuids[role]);

            var old = new ExternalDefinition[Case7RoleCount];
            for (var role = 0; role < Case7RoleCount; role++)
                old[role] = Create(
                    Case8OldGroupName, Case7OldNames[0][role]!, Case7OldGuids[0][role]!.Value);

            var note = Create(Case8OldGroupName, Case8NoteName, Case8NoteGuid);
            return new Case8Definitions(targets, old, note);
        }
        catch (Exception ex)
        {
            _case8SeedError = "Не удалось засидировать определения ФОП кейса 8: " +
                               ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private void BindCase8Parameters(Document doc, Case8Definitions definitions)
    {
        try
        {
            var app = doc.Application;
            var category = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel);
            var categorySet = app.Create.NewCategorySet();
            categorySet.Insert(category);
            var typeBinding = app.Create.NewTypeBinding(categorySet);
            foreach (var definition in definitions.Targets.Concat(new[] { definitions.Note })
                         .Concat(definitions.Old))
            {
                if (!doc.ParameterBindings.Insert(definition, typeBinding))
                {
                    _case8SeedError = "Не удалось привязать общий параметр «" +
                                       definition.Name + "» к категории";
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _case8SeedError = "Привязка общих параметров кейса 8: " +
                               ex.GetType().Name + ": " + ex.Message;
        }
    }

    private void PlaceCase8Instances(Document doc)
    {
        var placed = 0;
        foreach (var row in _case8Truth)
        {
            if (doc.GetElement(ElementIdCompat.Create(row.TypeId)) is not FamilySymbol symbol)
                continue;

            symbol.Activate();
            var instance = doc.Create.NewFamilyInstance(
                new XYZ((placed % 3) * 6.0, (placed / 3) * 6.0, 0.0),
                symbol,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            row.InstanceId = instance.Id.GetValue();
            placed++;
        }
    }

    private void SeedCase8Values(Document doc, Case8Definitions definitions)
    {
        foreach (var row in _case8Truth)
        {
            if (doc.GetElement(ElementIdCompat.Create(row.TypeId)) is not ElementType type)
                continue;

            for (var role = 0; role < Case7RoleCount; role++)
            {
                type.get_Parameter(definitions.Old[role].GUID)?.Set(row.Old[role]);
                type.get_Parameter(definitions.Targets[role].GUID)?.Set(row.New[role]);
            }
        }
    }

    private void SeedCase8Schedules(Document doc)
    {
        var big = Case8NewSchedule(doc, Case8BigName);
        var middle = Case8NewSchedule(doc, Case8MiddleName);
        var simple = Case8NewSchedule(doc, Case8SimpleName);
        var bait = Case8NewSchedule(doc, Case8BaitName);
        if (_case8SeedError is not null)
            return;

        SeedCase8BigSchedule(doc, big!);
        SeedCase8MiddleSchedule(doc, middle!);
        SeedCase8SimpleSchedule(doc, simple!);

        var baitDefinition = bait!.Definition;
        if (!TryAddCaseSharedField(doc, baitDefinition, Case7OldGuids[0][1]!.Value))
        {
            _case8SeedError = "Поле приманки «" + Case7OldNames[0][1] + "» не найдено";
            return;
        }

        var typeField = FindCaseSchedulableFieldByName(
            doc, baitDefinition, "Тип", "Type", "Типоразмер", "Type Name");
        if (typeField is not null)
            baitDefinition.AddField(typeField);

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
            _case8SeedError = "В шаблоне нет Title Block — листовые ведомости невозможны";
            return;
        }

        var bigSheet = ViewSheet.Create(doc, titleBlockId);
        bigSheet.SheetNumber = Case8BigSheetNumber;
        bigSheet.Name = "Ведомость объёма работ";
        var sharedSheet = ViewSheet.Create(doc, titleBlockId);
        sharedSheet.SheetNumber = Case8SharedSheetNumber;
        sharedSheet.Name = "Сводные ведомости";

        Case8Place(doc, bigSheet, big!, 120, 60);
        Case8Place(doc, sharedSheet, middle!, 120, 40);
        Case8Place(doc, sharedSheet, simple!, 120, 180);
        _case8BaitId = bait.Id.GetValue();
        _case8SheetsCreated = true;
    }

    private ViewSchedule? Case8NewSchedule(Document doc, string name)
    {
        try
        {
            var schedule = ViewSchedule.CreateSchedule(
                doc, new ElementId(BuiltInCategory.OST_GenericModel));
            schedule.Name = name;
            return schedule;
        }
        catch (Exception ex)
        {
            _case8SeedError = "Не удалось создать ведомость «" + name + "»: " +
                               ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private void SeedCase8BigSchedule(Document doc, ViewSchedule schedule)
    {
        var definition = schedule.Definition;
        ScheduleField? AddOld(int role, string heading, double widthMm)
        {
            var schedulable = Case8OldField(doc, definition, role);
            if (schedulable is null)
                return null;

            var field = definition.AddField(schedulable);
            field.ColumnHeading = heading;
            AgentScheduleWriteHelper.TrySetColumnWidthMm(field, widthMm, out _);
            return field;
        }

        AddOld(0, "Наименование", 70);
        AddOld(1, "Марка", 35);
        AddOld(2, "Ед. изм.", 18);
        var plant = AddOld(3, "Завод-изготовитель", 45);
        if (plant is not null)
            plant.IsHidden = true;
        AddOld(4, "Артикул", 30);

        var familyField = Case8AddBuiltin(doc, definition, 40, "Семейство", "Family");
        var typeField = Case8AddBuiltin(doc, definition, 35, "Тип", "Type", "Типоразмер", "Type Name");
        Case8AddCount(definition, 15);
        var noteSchedulable = Case8OldField(doc, definition, Case8NoteGuid, Case8NoteName);
        if (noteSchedulable is not null)
        {
            var note = definition.AddField(noteSchedulable);
            note.ColumnHeading = "Примечание";
            AgentScheduleWriteHelper.TrySetColumnWidthMm(note, 40, out _);
        }

        // Сортировка 2 уровня с флагами: Семейство (+итоги/пустая строка), Марка.
        try
        {
            if (familyField is not null)
                definition.AddSortGroupField(new ScheduleSortGroupField(
                    familyField.FieldId, ScheduleSortOrder.Ascending)
                {
                    ShowFooter = true,
                    ShowBlankLine = true
                });
            if (typeField is not null)
                definition.AddSortGroupField(new ScheduleSortGroupField(
                    typeField.FieldId, ScheduleSortOrder.Ascending));
        }
        catch (Exception)
        {
            // Сортировка builtin-полей недоступна в шаблоне — спека останется
            // однопорядковой, вердикт сравнивает новую с архивом как есть.
        }

        var unitFieldId = Case8OldFieldId(doc, definition, 2);
        if (unitFieldId is not null)
        {
            try
            {
                definition.AddFilter(new ScheduleFilter(
                    unitFieldId, ScheduleFilterType.Equal, "шт"));
            }
            catch (Exception)
            {
                // Поле нефильтруемо в этом шаблоне — спека останется без фильтра,
                // вердикт сравнивает новую с архивом как есть.
            }
        }

        try
        {
            definition.IsItemized = false;
        }
        catch (Exception)
        {
            // Key schedules refuse the flag; this one is a plain schedule.
        }

        definition.ShowGrandTotal = true;
        definition.ShowGrandTotalCount = true;
        schedule.HasStripedRows = true;
        schedule.UseStripedRowsOnSheets = true;
    }

    private void SeedCase8MiddleSchedule(Document doc, ViewSchedule schedule)
    {
        var definition = schedule.Definition;
        foreach (var (role, heading, widthMm) in new[]
                 {
                     (0, "Наименование", 65.0), (1, "Марка", 30.0), (2, "Ед. изм.", 18.0)
                 })
        {
            var schedulable = Case8OldField(doc, definition, role);
            if (schedulable is null)
                continue;

            var field = definition.AddField(schedulable);
            field.ColumnHeading = heading;
            AgentScheduleWriteHelper.TrySetColumnWidthMm(field, widthMm, out _);
        }

        Case8AddBuiltin(doc, definition, 35, "Семейство", "Family");
        Case8AddCount(definition, 15);
        var nameFieldId = Case8OldFieldId(doc, definition, 0);
        if (nameFieldId is not null)
        {
            try
            {
                definition.AddSortGroupField(new ScheduleSortGroupField(
                    nameFieldId, ScheduleSortOrder.Ascending));
            }
            catch (Exception)
            {
                // Несортируемое поле — остаётся без сортировки.
            }
        }
    }

    private void SeedCase8SimpleSchedule(Document doc, ViewSchedule schedule)
    {
        var definition = schedule.Definition;
        foreach (var (role, heading, widthMm) in new[]
                 {
                     (0, "Наименование", 60.0), (2, "Ед. изм.", 18.0)
                 })
        {
            var schedulable = Case8OldField(doc, definition, role);
            if (schedulable is null)
                continue;

            var field = definition.AddField(schedulable);
            field.ColumnHeading = heading;
            AgentScheduleWriteHelper.TrySetColumnWidthMm(field, widthMm, out _);
        }

        Case8AddCount(definition, 15);
    }

    /// <summary>
    /// Schedulable-поле старого параметра по роли; при отсутствии — ошибка сида
    /// (Skip после транзакции), а не исключение внутри транзакции.
    /// </summary>
    private SchedulableField? Case8OldField(
        Document doc, ScheduleDefinition definition, int role) =>
        Case8OldField(doc, definition, Case7OldGuids[0][role]!.Value, Case7OldNames[0][role]!);

    private SchedulableField? Case8OldField(
        Document doc, ScheduleDefinition definition, Guid guid, string displayName)
    {
        var shared = SharedParameterElement.Lookup(doc, guid);
        if (shared is not null)
        {
            foreach (var candidate in definition.GetSchedulableFields())
                if (candidate.ParameterId == shared.Id)
                    return candidate;
        }

        _case8SeedError = "Нет назначаемого поля для «" + displayName +
                           "» — спека кейса 8 не собрана";
        return null;
    }

    private static ScheduleFieldId? Case8OldFieldId(
        Document doc, ScheduleDefinition definition, int role)
    {
        var expected = Case7OldGuids[0][role]!.Value;
        foreach (var fieldId in definition.GetFieldOrder())
        {
            var field = definition.GetField(fieldId);
            if (field is null)
                continue;

            var parameterId = field.ParameterId;
            if (parameterId is not null &&
                doc.GetElement(parameterId) is SharedParameterElement shared &&
                shared.GuidValue == expected)
                return fieldId;
        }

        return null;
    }

    /// <summary>Builtin-колонка (Семейство/Тип) с шириной; null — поля нет в шаблоне.</summary>
    private static ScheduleField? Case8AddBuiltin(
        Document doc, ScheduleDefinition definition, double widthMm, params string[] names)
    {
        var schedulable = FindCaseSchedulableFieldByName(doc, definition, names);
        if (schedulable is null)
            return null;

        var field = definition.AddField(schedulable);
        AgentScheduleWriteHelper.TrySetColumnWidthMm(field, widthMm, out _);
        return field;
    }

    /// <summary>Колонка «Кол-во» (вычисляемое поле Count; оба варианта API).</summary>
    private static ScheduleField? Case8AddCount(ScheduleDefinition definition, double widthMm)
    {
        ScheduleField? field = null;
        try
        {
            field = definition.AddField(ScheduleFieldType.Count);
        }
        catch (Exception)
        {
            try
            {
                field = definition.AddField(
                    ScheduleFieldType.Count, ElementId.InvalidElementId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        AgentScheduleWriteHelper.TrySetColumnWidthMm(field, widthMm, out _);
        return field;
    }

    private void Case8Place(
        Document doc,
        ViewSheet sheet,
        ViewSchedule schedule,
        double xMm,
        double yMm)
    {
        ScheduleSheetInstance.Create(
            doc, sheet.Id, schedule.Id,
            new XYZ(
                AgentSheetWriteHelper.MillimetersToInternal(xMm),
                AgentSheetWriteHelper.MillimetersToInternal(yMm),
                0));
        _case8Placements.Add(new Case8Placement
        {
            Name = schedule.Name,
            ScheduleId = schedule.Id.GetValue(),
            SheetNumber = sheet.SheetNumber,
            PointMmX = xMm,
            PointMmY = yMm
        });
    }

    private void VerifyCase8Seed()
    {
        if (!_case8SheetsCreated || _case8Placements.Count != 3)
        {
            Skip.Test("Листовые ведомости кейса 8 не созданы (создано " +
                      _case8Placements.Count + " выносов)");
            return;
        }

        var sheetNumbers = AgentScheduleReader.MapSheetNumbers(Doc);
        foreach (var placement in _case8Placements)
        {
            if (!sheetNumbers.TryGetValue(placement.ScheduleId, out var numbers) ||
                !numbers!.Contains(placement.SheetNumber))
            {
                Skip.Test("Сид кейса 8 некорректен: «" + placement.Name + "» не на листе " +
                          placement.SheetNumber);
                return;
            }
        }

        if (sheetNumbers.TryGetValue(_case8BaitId, out _))
        {
            Skip.Test("Сид кейса 8 некорректен: приманка оказалась на листе");
            return;
        }

        var placedRows = _case8Truth.Count(row => row.InstanceId != 0);
        if (placedRows != 6)
            Skip.Test("Сид кейса 8 некорректен: размещено элементов " + placedRows + " (нужно 6)");
    }

    // ── Тулы ────────────────────────────────────────────────────────────

    private List<IAgentTool> CreateCase8Tools()
    {
        var context = new StubRevitContext(Doc);
        var decisions = AgentDecisionsSettings.Load();
        _case8Decisions = new HttpDecisionClient(decisions);

        var tools = new List<IAgentTool>
        {
            new ListSchedulesTool(context),
            new GetScheduleDefinitionTool(context),
            new GetScheduleDataTool(context),
            new ListSheetsTool(context),
            new ListFamiliesTool(context),
            new ListFamilyTypesTool(context),
            new GetProjectOverviewTool(context),
            new ListCategoriesTool(context),
            new QueryElementsTool(context),
            new CloneScheduleTool(context, _transactions!, _changeSets!),
            new RenameScheduleTool(context, _transactions!, _changeSets!),
            new RemoveScheduleFromSheetTool(context, _transactions!, _changeSets!),
            new PlaceScheduleOnSheetTool(context, _transactions!, _changeSets!),
            new SetScheduleFieldOrderTool(context, _transactions!, _changeSets!),
            new SemanticMatchTool(_case8Decisions, decisions),
            new UndoLastTool(context, _transactions!, _changeSets!)
        };

        AddCaseFindTools(tools);
        return tools;
    }

    private void CleanupCase8()
    {
        DisposeCaseGenericFamilies(_case8Families);
        _case8SharedFile?.Dispose();
        _case8Decisions?.Dispose();
        _case8Decisions = null;
    }
}

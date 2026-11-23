using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Seed of case 13: the temp shared-parameter file, the generic families of
/// the truth table with type-bound values (binding and values in SEPARATE
/// transactions — type parameters materialize only on commit), the sheet
/// schedule + the bait schedule outside sheets, and the seed verification.
/// Truth/verdict — in the Case13 core partial, verdict — in Case13.Evaluation.
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private void SeedCase13()
    {
        _document = BenchDocuments.NewBase(Application);
        _case13SeedError = null;
        _case13Items = new List<Case13ItemSeed>();
        _case13SharedFile = SharedParameterFileScope.Create(Application);

        var truth = Case13Truth();
        var kinds = Case13Kinds();
        var spec = truth
            .GroupBy(item => item.Family)
            .Select(group => (group.Key, group.Select(item => item.Type).ToArray()))
            .ToArray();
        var families = SeedCaseGenericFamilies(spec, out var skipReason);
        if (families is null)
        {
            Skip.Test(skipReason ?? "Сид-семейства кейса 13 не собрались");
            return;
        }

        // Определения ФОП создаются ПОСЛЕ загрузки семейств: перезапись тёзок
        // шаблона владельца заставляет Revit перечитывать ФОП, и обёртки,
        // полученные раньше, протухают (InvalidObjectExceptionProxy в Insert).
        var definitions = SeedCase13Definitions();
        if (_case13SeedError is not null)
        {
            Skip.Test(_case13SeedError);
            return;
        }

        _case13Families = families;
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        // Биндинг и запись значений — в РАЗНЫХ транзакциях: параметры type binding
        // материализуются на элементах только при коммите, get_Parameter(GUID) внутри
        // той же транзакции вернёт null и значения молча потеряются.
        _transactions.RunInTransaction(Doc, "Seed case 13 (bind)", doc =>
        {
            definitions = BindCase13Parameters(doc, definitions!);
            if (_case13SeedError is not null)
                return;

            PlaceCaseGenericInstances(doc, _case13Families);
        });

        if (_case13SeedError is not null)
        {
            Skip.Test(_case13SeedError);
            return;
        }


        _transactions.RunInTransaction(Doc, "Seed case 13 (audit)", doc =>
        {

            SeedCase13Items(doc, families, truth, kinds);
            if (_case13SeedError is not null)
                return;

            SeedCase13Schedules(doc);
        });

        if (_case13SeedError is not null)
        {
            Skip.Test(_case13SeedError);
            return;
        }

        VerifyCase13Seed();
    }

    private Case13Definitions? SeedCase13Definitions()
    {
        try
        {
            var file = Application.OpenSharedParameterFile();
            if (file is null)
            {
                _case13SeedError = "Временный ФОП кейса 13 не открылся";
                return null;
            }

            var group = file.Groups.Create(Case13SharedGroup);

            ExternalDefinition Create(string name, Guid guid) =>
                (ExternalDefinition)group.Definitions.Create(
                    new ExternalDefinitionCreationOptions(name, SpecTypeId.String.Text)
                    {
                        GUID = guid
                    });

            return new Case13Definitions(
                Create(Case13NameParameter, Case13NameGuid),
                Create(Case13MarkParameter, Case13MarkGuid),
                Create(Case13UnitParameter, Case13UnitGuid),
                Create(Case13CountParameter, Case13CountGuid),
                Create(Case13BaitParameter, Case13BaitGuid));
        }
        catch (Exception ex)
        {
            _case13SeedError = "Не удалось засидировать определения ФОП кейса 13: " +
                               ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>4 общих параметра — type binding; приманка «Комментарий» — instance.</summary>
    private Case13Definitions BindCase13Parameters(Document doc, Case13Definitions definitions)
    {
        try
        {
            var app = doc.Application;
            var category = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel);
            var categorySet = app.Create.NewCategorySet();
            categorySet.Insert(category);
            var typeBinding = app.Create.NewTypeBinding(categorySet);
            var instanceBinding = app.Create.NewInstanceBinding(categorySet);
            var map = doc.ParameterBindings;

            // Обёртка резолвится в момент КАЖДОГО Insert: между двумя Insert
            // Revit может перечитывать ФОП — см. док-комментарий
            // FindCaseSharedDefinition.
            foreach (var parameterName in new[]
                     {
                         Case13NameParameter, Case13MarkParameter,
                         Case13UnitParameter, Case13CountParameter
                     })
            {
                var definition = FindCaseSharedDefinition(Case13SharedGroup, parameterName);
                if (definition is null)
                {
                    _case13SeedError = "Общий параметр «" + parameterName +
                                       "» не найден в ФОП перед привязкой (обёртка протухла)";
                    return definitions;
                }

                if (!map.Insert(definition, typeBinding))
                {
                    _case13SeedError = "Не удалось привязать общий параметр «" +
                                       definition.Name + "» (type) к категории";
                    return definitions;
                }
            }

            var bait = FindCaseSharedDefinition(Case13SharedGroup, Case13BaitParameter);
            if (bait is null)
            {
                _case13SeedError = "Приманка «" + Case13BaitParameter +
                                   "» не найдена в ФОП перед привязкой (обёртка протухла)";
                return definitions;
            }

            definitions = definitions with { Bait = bait };
            if (!map.Insert(bait, instanceBinding))
            {
                _case13SeedError = "Не удалось привязать приманку «" +
                                   bait.Name + "» (instance)";
            }
        }
        catch (Exception ex)
        {
            _case13SeedError = "Привязка общих параметров кейса 13: " +
                               ex.GetType().Name + ": " + ex.Message + " @ " + ex.StackTrace;
        }

        return definitions;
    }

    /// <summary>
    /// Единый ключ «семейство/тип» для таблицы истины кейса 13: истина
    /// индексируется и ищется ТОЛЬКО через него (ранее имена сида и ключи
    /// истины расходились из-за temp-префикса .rfa — живой прогон 2026-11-22).
    /// </summary>
    private static string Case13KeyOf(string family, string type) => family + "/" + type;

    private void SeedCase13Items(
        Document doc,
        List<CaseGenericFamilySeed> families,
        (string Family, string Type, string? Name, string? Mark, string? Unit)[] truth,
        Dictionary<string, string> kinds)
    {
        var truthByKey = new Dictionary<
            string,
            (string Family, string Type, string? Name, string? Mark, string? Unit)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in truth)
            truthByKey[Case13KeyOf(row.Family, row.Type)] = row;

        foreach (var family in families)
        {
            foreach (var type in family.Types)
            {
                if (!truthByKey.TryGetValue(
                        Case13KeyOf(family.FamilyName, type.TypeName), out var row))
                {
                    _case13SeedError = "Нет строки истины для «" +
                                       Case13KeyOf(family.FamilyName, type.TypeName) + "»";
                    return;
                }

                _case13Items.Add(new Case13ItemSeed
                {
                    Family = family.FamilyName,
                    TypeName = type.TypeName,
                    TypeId = type.TypeId,
                    InstanceId = type.InstanceId,
                    Name = row.Name,
                    Mark = row.Mark,
                    Unit = row.Unit,
                    Kind = kinds.TryGetValue(family.FamilyName, out var kind)
                        ? kind
                        : string.Empty
                });

                if (doc.GetElement(ElementIdCompat.Create(type.TypeId)) is not ElementType typeElement)
                    continue;

                // null = дырка остаётся дыркой (HasValue=false → emptyNoValue);
                // GUID-константы вместо ExternalDefinition-обёрток (протухают
                // после перечитывания ФОП — см. FindCaseSharedDefinition).
                Case13Set(typeElement, Case13NameGuid, row.Name);
                Case13Set(typeElement, Case13MarkGuid, row.Mark);
                Case13Set(typeElement, Case13UnitGuid, row.Unit);
                Case13Set(typeElement, Case13CountGuid, "1");
            }
        }

        // Приманка: «Комментарий» заполнен только у оборудования (местами пустой).
        foreach (var item in _case13Items.Where(item => item.Kind == "equipment").ToList())
        {
            if (doc.GetElement(ElementIdCompat.Create(item.InstanceId)) is { } instance)
                instance.get_Parameter(Case13BaitGuid)?.Set("черновая прикидка");
        }
    }

    private static void Case13Set(Element element, Guid guid, string? value)
    {
        if (value is null)
            return;

        element.get_Parameter(guid)?.Set(value);
    }

    private void SeedCase13Schedules(Document doc)
    {
        var sheetSchedule = ViewSchedule.CreateSchedule(
            doc, new ElementId(BuiltInCategory.OST_GenericModel));
        sheetSchedule.Name = Case13ScheduleName;
        var definition = sheetSchedule.Definition;
        foreach (var guid in new[]
                 {
                     Case13NameGuid, Case13MarkGuid, Case13UnitGuid, Case13CountGuid
                 })
        {
            if (!TryAddCaseSharedField(doc, definition, guid))
            {
                _case13SeedError = "Поле общего параметра (guid …" +
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

        var baitSchedule = ViewSchedule.CreateSchedule(
            doc, new ElementId(BuiltInCategory.OST_GenericModel));
        baitSchedule.Name = Case13BaitScheduleName;
        if (!TryAddCaseSharedField(doc, baitSchedule.Definition, Case13BaitGuid))
        {
            _case13SeedError = "Поле приманки «" + Case13BaitParameter + "» не найдено";
            return;
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
            _case13SeedError = "В шаблоне нет Title Block — листовая ведомость невозможна";
            return;
        }

        var sheet = ViewSheet.Create(doc, titleBlockId);
        sheet.SheetNumber = Case13SheetNumber;
        sheet.Name = "Ведомость оборудования (лист)";
        ScheduleSheetInstance.Create(doc, sheet.Id, sheetSchedule.Id, new XYZ(0.1, 0.1, 0));
        _case13ScheduleId = sheetSchedule.Id.GetValue();
        _case13BaitScheduleId = baitSchedule.Id.GetValue();
        _case13SheetCreated = true;
    }

    private void VerifyCase13Seed()
    {
        if (!_case13SheetCreated)
        {
            Skip.Test("Листовая ведомость кейса 13 не создана");
            return;
        }

        var nameEmpty = _case13Items.Count(item => Case13IsEmpty(item, Case13NameGuid));
        var markEmpty = _case13Items.Count(item => Case13IsEmpty(item, Case13MarkGuid));
        var unitEmpty = _case13Items.Count(item => Case13IsEmpty(item, Case13UnitGuid));
        var countEmpty = _case13Items.Count(item => Case13IsEmpty(item, Case13CountGuid));
        if (_case13Items.Count != Case13ItemCount ||
            nameEmpty != 2 || markEmpty != 1 || unitEmpty != 4 || countEmpty != 0)
        {
            Skip.Test("Сид кейса 13 некорректен: позиций " + _case13Items.Count +
                      ", дырки Name/Mark/Unit/Count " + nameEmpty + "/" + markEmpty + "/" +
                      unitEmpty + "/" + countEmpty);
            return;
        }

        var sheetNumbers = AgentScheduleReader.MapSheetNumbers(Doc);
        var placed = sheetNumbers.TryGetValue(_case13ScheduleId, out var numbers) &&
                     numbers!.Count > 0;
        var baitPlaced = sheetNumbers.TryGetValue(_case13BaitScheduleId, out _);
        if (!placed || baitPlaced)
        {
            Skip.Test("Сид кейса 13 некорректен: листовая спека placed=" + placed +
                      ", приманка placed=" + baitPlaced);
            return;
        }

        var bait = Doc.GetElement(ElementIdCompat.Create(_case13BaitScheduleId)) as ViewSchedule;
        if (bait is null || !AgentScheduleReader.ReadColumns(Doc, bait, out _).Any(column =>
                ContainsText(column.Heading, Case13BaitParameter) ||
                ContainsText(column.ParameterName ?? string.Empty, Case13BaitParameter)))
        {
            Skip.Test("Сид кейса 13 некорректен: у приманки нет поля «" + Case13BaitParameter + "»");
        }
    }
}

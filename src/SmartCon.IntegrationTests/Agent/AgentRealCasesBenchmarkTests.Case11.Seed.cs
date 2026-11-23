using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Seed of case 11: the title block with the editable size-multiplicity
/// parameter (EditFamily round-trip), three systems each on its own
/// PipingSystemType duplicate (pipes + elbow fittings, Case15 mechanics),
/// the value/documentation transaction (3D views, the multi-category 9-column
/// main schedule, album sheets 1-3). Truth/verdict — in the Case11 core
/// partial, verdict — in Case11.Evaluation.
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private void SeedCase11()
    {
        // MEP-шаблон установки: нужны PipeType/PipingSystemType (паттерн кейса 10/15).
        _document = SampleFiles.NewMepTemplateDocument(Application);
        _document ??= BenchDocuments.NewBase(Application);
        if (!ModelSeed.HasPipeTypes(Doc))
        {
            Skip.Test("В шаблоне проекта нет типов труб — сид кейса 11 невозможен");
            return;
        }

        _case11SeedError = null;
        _case11Systems = new List<Case11SystemSeed>();
        _case11Album = new List<Case11SheetSeed>();
        _case11SharedFile = SharedParameterFileScope.Create(Application);
        var definitions = SeedCase11Definitions();
        if (_case11SeedError is not null)
        {
            Skip.Test(_case11SeedError);
            return;
        }

        // Загрузка титблока + EditFamily-параметр кратности — ВНЕ транзакций
        // (LoadFamily/LoadFamily(back) на изменяемом документе падает).
        if (!PrepareCase11TitleBlock())
        {
            Skip.Test(_case11SeedError ?? "Титульный блок кейса 11 не подготовлен");
            return;
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        // Транзакция 1: системы (трубы + фитинги + привязка общих параметров).
        _transactions.RunInTransaction(Doc, "Seed case 11 (systems)", doc =>
        {
            try
            {
                SeedCase11Systems(doc, definitions!);
            }
            catch (Exception ex)
            {
                _case11SeedError = "Сид систем кейса 11: " + ex.GetType().Name + ": " + ex.Message;
            }
        });

        if (_case11SeedError is not null)
        {
            Skip.Test(_case11SeedError);
            return;
        }

        // Транзакция 2: значения параметров (материализуются после коммита привязки —
        // механика кейса 13), 3D-виды, общая спецификация, альбом.
        _transactions.RunInTransaction(Doc, "Seed case 11 (documentation)", doc =>
        {
            try
            {
                SeedCase11Documentation(doc, definitions!);
            }
            catch (Exception ex)
            {
                _case11SeedError = "Сид документации кейса 11: " + ex.GetType().Name + ": " + ex.Message;
            }
        });

        if (_case11SeedError is not null)
        {
            Skip.Test(_case11SeedError);
            return;
        }

        VerifyCase11Seed();
    }

    /// <summary>
    /// Титульный блок с редактируемым параметром кратности: загрузка .rfa из
    /// библиотеки установки → EditFamily → FamilyManager.AddParameter
    /// («Кратность размера», Integer, instance) → LoadFamily обратно.
    /// </summary>
    private bool PrepareCase11TitleBlock()
    {
        var titleBlockId = ModelSeed.FindTitleBlockTypeId(Doc);
        if (titleBlockId is null)
        {
            var path = ModelSeed.FindTitleBlockFamilyFile(Doc.Application.VersionNumber);
            if (path is null)
            {
                _case11SeedError = "В библиотеке установки нет .rfa титульного блока — сид кейса 11 невозможен";
                return false;
            }

            try
            {
                if (!Doc.LoadFamily(path, out _))
                {
                    _case11SeedError = "Не удалось загрузить титульный блок " + path;
                    return false;
                }
            }
            catch (Exception ex)
            {
                _case11SeedError = "Загрузка титульного блока: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            titleBlockId = ModelSeed.FindTitleBlockTypeId(Doc);
        }

        if (Doc.GetElement(titleBlockId ?? ElementId.InvalidElementId) is not FamilySymbol symbol ||
            symbol.Family is not { } family)
        {
            _case11SeedError = "Тип титульного блока не читается после загрузки";
            return false;
        }

        _case11TitleBlockFamilyName = family.Name;

        Document? familyDoc = null;
        try
        {
            familyDoc = Doc.EditFamily(family);
            using (var transaction = new Transaction(familyDoc, "Add size multiplicity"))
            {
                transaction.Start();
                familyDoc.FamilyManager.AddParameter(
                    Case11TitleblockMultiplicity, GroupTypeId.Data, SpecTypeId.Int.Integer,
                    isInstance: true);
                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    _case11SeedError = "Транзакция параметра кратности титульного семейства не завершилась";
                    return false;
                }
            }

            var loaded = familyDoc.LoadFamily(Doc, new OverwriteFamilyLoadOptions());
            if (loaded is null)
            {
                _case11SeedError = "Титульное семейство с кратностью не загрузилось обратно в проект";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _case11SeedError = "EditFamily титульного блока кейса 11: " +
                               ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            CloseCaseFamilyDoc(familyDoc);
        }
    }

    /// <summary>
    /// Три системы, каждая — на СВОЁМ дубликате PipingSystemType (разные типы
    /// гарантируют разные MEPSystem), трубы + колена-фитинги на изломах
    /// (Case15-механика), переименование логической системы в имя кейса.
    /// </summary>
    private void SeedCase11Systems(Document doc, Case11Definitions definitions)
    {
        var level = ModelSeed.CreateLevel(doc, 0.0);
        level.Name = "Этаж 1";

        var pipeTypeId = BenchFirstTypeId(doc, typeof(PipeType));
        var baseSystemType = doc.GetElement(BenchFirstTypeId(doc, typeof(PipingSystemType))) as ElementType;
        if (pipeTypeId is null || baseSystemType is null)
        {
            _case11SeedError = "PipeType/PipingSystemType исчезли после проверки шаблона";
            return;
        }

        // Привязка общих параметров к трубам и фитингам (instance binding) —
        // до записи значений, значения в следующей транзакции (механика кейса 13).
        var app = doc.Application;
        var categorySet = app.Create.NewCategorySet();
        categorySet.Insert(Category.GetCategory(doc, BuiltInCategory.OST_PipeCurves));
        categorySet.Insert(Category.GetCategory(doc, BuiltInCategory.OST_PipeFitting));
        var binding = app.Create.NewInstanceBinding(categorySet);
        foreach (var definition in new[]
                 {
                     definitions.Name, definitions.Article, definitions.Mark,
                     definitions.Unit, definitions.Count, definitions.SystemName
                 })
        {
            if (!doc.ParameterBindings.Insert(definition, binding))
            {
                _case11SeedError = "Не удалось привязать общий параметр «" +
                                   definition.Name + "» к трубам/фитингам";
                return;
            }
        }

        var routes = Case11Routes();
        for (var index = 0; index < Case11Systems.Length; index++)
        {
            var name = Case11Systems[index];
            var points = routes[index];
            var seed = new Case11SystemSeed { Name = name };
            var systemTypeId = baseSystemType.Duplicate("SC_Кейс11_" + name.Replace('.', '_')).Id;

            Pipe? previous = null;
            for (var segment = 1; segment < points.Length; segment++)
            {
                var pipe = Pipe.Create(
                    doc, systemTypeId, pipeTypeId, level.Id, points[segment - 1], points[segment]);
                seed.PipeIds.Add(pipe.Id.GetValue());
                seed.RouteLengthFt += points[segment - 1].DistanceTo(points[segment]);
                doc.Regenerate();

                if (previous is not null)
                {
                    var joint = points[segment - 1];
                    var elbow = doc.Create.NewElbowFitting(
                        ModelSeed.FindConnectorAt(previous, joint),
                        ModelSeed.FindConnectorAt(pipe, joint));
                    seed.FittingIds.Add(elbow.Id.GetValue());
                    doc.Regenerate();
                }

                previous = pipe;
            }

            Case15RenameElement(doc.GetElement(previous!.MEPSystem?.Id ?? ElementId.InvalidElementId), name);
            doc.Regenerate();
            _case11Systems.Add(seed);
        }
    }

    private void SeedCase11Documentation(Document doc, Case11Definitions definitions)
    {
        // 1. Значения общих параметров всем элементам систем (наследие кейса 4:
        // «АДСК_ИмяСистемы» заполнен всем, включая фитинги).
        foreach (var system in _case11Systems)
        {
            foreach (var pipeId in system.PipeIds)
                Case11FillElement(doc, definitions, pipeId, system.Name, isFitting: false);
            foreach (var fittingId in system.FittingIds)
                Case11FillElement(doc, definitions, fittingId, system.Name, isFitting: true);
        }

        // 2. 3D-вид на каждую систему (фильтр не нужен: кейс считает его сделанным).
        var viewType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(type => type.ViewFamily == ViewFamily.ThreeDimensional);
        if (viewType is null)
        {
            _case11SeedError = "В шаблоне проекта нет ViewFamilyType для 3D-вида";
            return;
        }

        foreach (var system in _case11Systems)
        {
            var view = View3D.CreateIsometric(doc, viewType.Id);
            view.Name = system.Name;
            system.ViewId = view.Id.GetValue();
        }

        // 3. Общая спецификация — мультикатегорийная (трубы И фитинги в одной
        // 9-графке): 6 общих полей + до 3 служебных built-in.
        SeedCase11MainSchedule(doc);

        // 4. Альбом 1..3 с кратностью 1.
        var titleBlockId = ModelSeed.FindTitleBlockTypeId(doc);
        if (titleBlockId is null)
        {
            _case11SeedError = "Тип титульного блока исчез после EditFamily-цикла";
            return;
        }

        var albumNames = new[] { "Общие данные", "План 1 этажа", "План 2 этажа" };
        for (var index = 0; index < albumNames.Length; index++)
        {
            var sheet = ViewSheet.Create(doc, titleBlockId);
            sheet.SheetNumber = (index + 1).ToString(CultureInfo.InvariantCulture);
            sheet.Name = albumNames[index];
            _case11Album.Add(new Case11SheetSeed
            {
                Number = (index + 1).ToString(CultureInfo.InvariantCulture),
                Name = albumNames[index],
                Id = sheet.Id.GetValue()
            });
        }

        doc.Regenerate();
        foreach (var album in _case11Album)
        {
            if (doc.GetElement(ElementIdCompat.Create(album.Id)) is not ViewSheet sheet)
                continue;

            Case11TitleBlockOf(sheet)?.LookupParameter(Case11TitleblockMultiplicity)?.Set(1);
        }
    }

    private void SeedCase11MainSchedule(Document doc)
    {
        ViewSchedule schedule;
        try
        {
            schedule = ViewSchedule.CreateSchedule(doc, ElementId.InvalidElementId);
        }
        catch (Exception ex)
        {
            _case11SeedError = "Мультикатегорийная спецификация не создалась: " +
                               ex.GetType().Name + ": " + ex.Message;
            return;
        }

        schedule.Name = Case11MainScheduleName;
        var definition = schedule.Definition;
        foreach (var guid in new[]
                 {
                     Case11NameGuid, Case11ArticleGuid, Case11MarkGuid,
                     Case11UnitGuid, Case11CountGuid, Case11SystemNameGuid
                 })
        {
            if (!TryAddCaseSharedField(doc, definition, guid))
            {
                _case11SeedError = "Общее поле (guid …" +
                                   guid.ToString("D").Substring(guid.ToString("D").Length - 6) +
                                   ") не найдено среди schedulable мультикатегорийной спеки";
                return;
            }
        }

        // Служебные built-in: до трёх из кандидатов (состав зависит от локализации).
        var added = 0;
        foreach (var candidates in new[]
                 {
                     new[] { "Семейство", "Family" },
                     new[] { "Тип", "Type Name", "Типоразмер" },
                     new[] { "Комментарий", "Comments" }
                 })
        {
            if (added >= 3)
                break;

            var field = FindCaseSchedulableFieldByName(doc, definition, candidates);
            if (field is not null)
            {
                definition.AddField(field);
                added++;
            }
        }

        _case11ScheduleColumns = AgentScheduleReader.ReadColumns(Doc, schedule, out _).Count;
        _case11MainScheduleId = schedule.Id.GetValue();
    }
}

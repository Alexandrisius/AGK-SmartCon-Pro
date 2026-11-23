using System.IO;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Transactions;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Seed of case 14: the .rfa family files of the truth table, the donor .rvt
/// (levels, 4 pattern-named views, families with the filled mark — binding
/// and values in separate transactions, SaveAs + Close), and the active model
/// with the same families under an EMPTY mark and 4 junk-named views.
/// Truth/verdict — in the Case14 core partial, verdict — in
/// Case14.Evaluation. Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
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

                // Обёртка определения могла протухнуть при загрузке семейств
                // (Revit перечитывает ФОП; managed-IsValidObject врёт) —
                // безусловный ре-резолв перед биндингом.
                var freshDefinition = FindCaseSharedDefinition(
                    Case14SharedGroup, Case14MarkParameter);
                if (freshDefinition is null)
                {
                    _case14SeedError = "Определение «" + Case14MarkParameter +
                                       "» протухло после загрузки семейств и не нашлось в ФОП";
                    return;
                }

                Case14BindMark(doc, freshDefinition);
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
}

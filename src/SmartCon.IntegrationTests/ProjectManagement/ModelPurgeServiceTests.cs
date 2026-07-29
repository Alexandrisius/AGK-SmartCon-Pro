using Autodesk.Revit.DB;
using SmartCon.Core.Models;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Sharing;

namespace SmartCon.IntegrationTests.ProjectManagement;

/// <summary>
/// RevitModelPurgeService: реальная очистка модели перед Share (шаг 5 алгоритма,
/// 12 категорий PurgeOptions). Виды чистятся всегда по keepViewNames,
/// остальные категории — по флагам.
/// </summary>
public sealed class ModelPurgeServiceTests : ProjectViewsFixture
{
    private const string SheetName = "SC_Sheet";

    private RevitModelPurgeService? _purge;
    private RevitModelPurgeService Purge => _purge!;

    [Before(Test)]
    public void CreateService()
    {
        _purge = new RevitModelPurgeService(Transactions);
    }

    [Test]
    public async Task Purge_KeepViewNames_PreservesListedAndTemplates_DeletesRest()
    {
        // Arrange — PurgeUnused выключен: PerformanceAdviser недетерминирован
        var options = new PurgeOptions { PurgeUnused = false };

        // Act
        var deleted = Purge.Purge(Doc, options, [KeepViewName]);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(ViewExists(KeepViewName)).IsTrue();
            await Assert.That(ViewExists(DeleteViewName)).IsFalse();
            await Assert.That(ViewExists(TemplateViewName)).IsTrue();  // шаблоны не трогаем
            await Assert.That(ViewExists(ScheduleName)).IsFalse();     // PurgeSchedules по умолчанию true
            await Assert.That(deleted).IsGreaterThanOrEqualTo(3);
        }
    }

    [Test]
    public async Task Purge_AllCategoryFlagsFalse_StillPurgesViewsButKeepsSchedules()
    {
        // Arrange — важная семантика: виды чистятся ВСЕГДА (фильтр по имени),
        // независимо от флагов категорий
        var options = new PurgeOptions
        {
            PurgeRvtLinks = false,
            PurgeCadImports = false,
            PurgeImages = false,
            PurgePointClouds = false,
            PurgeGroups = false,
            PurgeAssemblies = false,
            PurgeSpaces = false,
            PurgeRebar = false,
            PurgeFabricReinforcement = false,
            PurgeSheets = false,
            PurgeSchedules = false,
            PurgeUnused = false
        };

        // Act
        _ = Purge.Purge(Doc, options, [KeepViewName]);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(ViewExists(DeleteViewName)).IsFalse();
            await Assert.That(ViewExists(ScheduleName)).IsTrue(); // PurgeSchedules=false
            await Assert.That(ViewExists(KeepViewName)).IsTrue();
        }
    }

    [Test]
    public async Task Purge_SheetsFlag_TogglesSheetDeletion()
    {
        // Arrange — регрессионный тест #176: ViewSheet наследуется от View и
        // подметался общим свипом pass 2 независимо от флага PurgeSheets.
        // Если в шаблоне нет Title Block — догружаем из контент-библиотеки Revit.
        var titleBlockId = ModelSeed.FindTitleBlockTypeId(Doc);
        if (titleBlockId is null)
        {
            var titleBlockPath = ModelSeed.FindTitleBlockFamilyFile(Application.VersionNumber);
            if (titleBlockPath is not null)
            {
                Transactions.RunInTransaction(Doc, "Load title block", doc =>
                {
                    doc.LoadFamily(titleBlockPath, out _);
                });
                titleBlockId = ModelSeed.FindTitleBlockTypeId(Doc);
            }
        }

        if (titleBlockId is null)
        {
            Skip.Test("Ни в шаблоне проекта, ни в контент-библиотеке Revit нет типа основной надписи (Title Block)");
        }

        Transactions.RunInTransaction(Doc, "Seed sheet", doc =>
        {
            var sheet = ViewSheet.Create(doc, titleBlockId);
            sheet.Name = SheetName;
        });

        // Act 1 — флаг выключен: лист обязан выжить
        _ = Purge.Purge(Doc, new PurgeOptions
        {
            PurgeRvtLinks = false,
            PurgeCadImports = false,
            PurgeImages = false,
            PurgePointClouds = false,
            PurgeGroups = false,
            PurgeAssemblies = false,
            PurgeSpaces = false,
            PurgeRebar = false,
            PurgeFabricReinforcement = false,
            PurgeSheets = false,
            PurgeSchedules = false,
            PurgeUnused = false
        }, [KeepViewName]);

        // Assert 1
        await Assert.That(ViewExists(SheetName)).IsTrue();

        // Act 2 — флаг включён: лист удаляется
        _ = Purge.Purge(Doc, new PurgeOptions { PurgeUnused = false }, [KeepViewName]);

        // Assert 2
        await Assert.That(ViewExists(SheetName)).IsFalse();
    }
}


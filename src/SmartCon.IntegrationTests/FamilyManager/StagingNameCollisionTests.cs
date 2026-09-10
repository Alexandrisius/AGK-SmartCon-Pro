using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Регрессионный тест бага ручного теста (Issue #104): staging мини-проекта
/// в шаблон, уже содержащий тип с тем же именем, не должен приводить к
/// авторенейму («Стена 1» → «Стена 2») — шаблонный тип переименовывается
/// до копирования, имя источника сохраняется. Переименованный шаблонный
/// тип НЕ удаляется (Revit запрещает удаление последнего типа системной
/// семьи, в UI-сессии — модальный диалог) и безвреден для каталога.
/// </summary>
public sealed class StagingNameCollisionTests : RevitApiTest
{
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Staging_TemplateCollision_SourceNameSurvives()
    {
        var sourceDoc = Application.NewProjectDocument(UnitSystem.Metric);
        var miniDoc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            // Базовый тип шаблона (со структурой), гарантированно
            // присутствующий в обоих документах.
            var templateTypeName = new FilteredElementCollector(miniDoc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .Where(t =>
                {
                    try
                    {
                        using var cs = t.GetCompoundStructure();
                        return cs is not null;
                    }
                    catch
                    {
                        return false;
                    }
                })
                .Select(t => t.Name)
                .FirstOrDefault();
            if (templateTypeName is null)
            {
                Skip.Test("В шаблоне нет базового WallType");
            }

            // В источнике — тип с ТЕМ ЖЕ именем, но отличительной толщиной.
            var sourceTx = new RevitTransactionService(new StubRevitContext(sourceDoc));
            ElementId sourceTypeId = null!;
            sourceTx.RunInTransaction(sourceDoc, "Make colliding source type", doc =>
            {
                var wallType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .First(t => t.Name == templateTypeName);
                using var cs = wallType.GetCompoundStructure();
                if (cs is null) return;
                var layers = cs.GetLayers();
                layers[0].Width = 1.0;
                cs.SetLayers(layers);
                wallType.SetCompoundStructure(cs);
                sourceTypeId = wallType.Id;
            });

            // Staging-последовательность: rename шаблонных типов + копирование.
            // Переименованный шаблонный тип НЕ удаляется — Revit запрещает
            // удаление последнего типа системной семьи (в UI-сессии — модальный
            // диалог и зависание batch-импорта); оставшийся тип безвреден.
            var miniTx = new RevitTransactionService(new StubRevitContext(miniDoc));
            TemplateCollisionResolver.RenameConflictingTemplateTypes(
                miniTx, miniDoc, BuiltInCategory.OST_Walls, new[] { templateTypeName });

            miniTx.RunInTransaction(miniDoc, "Copy system types", doc =>
            {
                var options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new SkipDuplicateTypesHandler());
                ElementTransformUtils.CopyElements(
                    sourceDoc, new List<ElementId> { sourceTypeId }, doc, null, options);
            });

            using (Assert.Multiple())
            {
                // Имя источника сохранилось ровно один раз, авторенейма нет.
                var names = new FilteredElementCollector(miniDoc)
                    .OfClass(typeof(WallType))
                    .Select(t => t.Name)
                    .ToList();
                await Assert.That(names.Count(n => n == templateTypeName)).IsEqualTo(1);
                await Assert.That(names.Any(n => n == templateTypeName + " 2")).IsFalse();

                // Содержимое — из источника (отличительная толщина 1.0 ft).
                var copied = new FilteredElementCollector(miniDoc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .First(t => t.Name == templateTypeName);
                await Assert.That(copied.Width).IsEqualTo(1.0);
            }
        }
        finally
        {
            sourceDoc.Close(false);
            miniDoc.Close(false);
        }
    }

    private sealed class SkipDuplicateTypesHandler : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
        {
            return DuplicateTypeAction.UseDestinationTypes;
        }
    }
}

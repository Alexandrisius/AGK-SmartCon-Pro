using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;

namespace SmartCon.IntegrationTests.Agent.Support;

/// <summary>
/// Общий сид «проектное семейство-хозяин с вложенным SHARED-семейством» —
/// механика, доказанная сидом кейса 4 бенчмарка (C4-04): flange-документ
/// (FAMILY_SHARED=1 + тип) → SaveAs → LoadFamily В документ хозяина (это и
/// есть вложение) → хозяин SaveAs → файловая загрузка хозяина в проект
/// ВНУТРИ открытой транзакции после удаления тёзок-конфликтов. Строчный
/// LoadFamily(string) вне транзакции молча возвращает false (загрузка меняет
/// базу проекта), а open-doc загрузка не материализует вложенный
/// shared-инстанс как субкомпонент. Используется сидом кейса 4 и IT джобы
/// set_parameters (includeSubComponents).
/// </summary>
internal static class NestedSharedFamilySeed
{
    public sealed class Result
    {
        public bool Seeded { get; init; }

        public string? Note { get; init; }

        public long HostSymbolId { get; init; }
    }

    /// <param name="hostFamilyName">Имя семейства-хозяина (и имя .rfa).</param>
    /// <param name="tempRoot">Каталог под временные .rfa (создаётся одноразовый
    /// подкаталог с GUID).</param>
    /// <param name="tempPaths">Коллекция вызовающего для cleanup: оба пути
    /// добавляются до попытки — файлы удаляются и при неудаче сида.</param>
    public static Result TrySeed(
        Application application,
        Document projectDoc,
        string hostFamilyName,
        string tempRoot,
        List<string> tempPaths)
    {
        var template = SampleFiles.FindFamilyTemplate(application);
        if (template is null)
            return Fail("В установленном Revit нет family-шаблона (.rft)");

        var directory = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var flangePath = Path.Combine(directory, "flange.rfa");
        var hostPath = Path.Combine(directory, hostFamilyName + ".rfa");
        tempPaths.Add(flangePath);
        tempPaths.Add(hostPath);

        Document? flangeDoc = null;
        Document? hostDoc = null;
        try
        {
            flangeDoc = application.NewFamilyDocument(template);
            using (var transaction = new Transaction(flangeDoc, "Mark shared flange"))
            {
                transaction.Start();
                flangeDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
                // Шаблон без типа семейства не даёт FamilySymbol при вложении.
                if (flangeDoc.FamilyManager.CurrentType is null)
                    flangeDoc.FamilyManager.NewType("Тип1");
                if (transaction.Commit() != TransactionStatus.Committed)
                    return Fail("Транзакция фланца не завершилась");
            }

            flangeDoc.SaveAs(flangePath, new SaveAsOptions { OverwriteExistingFile = true });

            hostDoc = application.NewFamilyDocument(template);
            using (var transaction = new Transaction(hostDoc, "Name host family"))
            {
                transaction.Start();
                hostDoc.OwnerFamily.Name = hostFamilyName;
                transaction.Commit();
            }

            var flangeFamily = flangeDoc.LoadFamily(hostDoc, new NestLoadOptions());
            if (flangeFamily is null)
                return Fail("Фланец не загрузился в семейство-хозяин");

            var flangeSymbolId = flangeFamily.GetFamilySymbolIds().FirstOrDefault();
            if (flangeSymbolId is null || hostDoc.GetElement(flangeSymbolId) is not FamilySymbol flangeSymbol)
                return Fail("У фланца нет типоразмера в семействе-хозяине");

            using (var transaction = new Transaction(hostDoc, "Nest shared flange"))
            {
                transaction.Start();
                flangeSymbol.Activate();
                hostDoc.FamilyCreate.NewFamilyInstance(
                    XYZ.Zero, flangeSymbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                if (transaction.Commit() != TransactionStatus.Committed)
                    return Fail("Вложение фланца в хозяина не завершилось");
            }

            hostDoc.SaveAs(hostPath, new SaveAsOptions { OverwriteExistingFile = true });
            hostDoc.Close(false);
            hostDoc = null;

            RemoveConflictingFamilies(projectDoc, hostFamilyName);
            Family? loadedHost;
            using (var transaction = new Transaction(projectDoc, "Seed nested host family"))
            {
                transaction.Start();
                var loaded = projectDoc.LoadFamily(hostPath, new NestLoadOptions(), out var family);
                var committed = transaction.Commit() == TransactionStatus.Committed;
                loadedHost = loaded && committed ? family : null;
            }

            if (loadedHost is null)
                return Fail("Семейство-хозяин не загрузилось в проект из файла " +
                            "(file LoadFamily в транзакции)");

            var hostSymbolId = loadedHost.GetFamilySymbolIds().FirstOrDefault();
            if (hostSymbolId is null)
                return Fail("У хозяина нет типоразмера в проекте");

            flangeDoc.Close(false);
            flangeDoc = null;
            return new Result
            {
                Seeded = true,
                HostSymbolId = hostSymbolId.GetValue()
            };
        }
        catch (Exception ex)
        {
            return Fail("Сид вложенного семейства упал: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            foreach (var open in new[] { flangeDoc, hostDoc })
            {
                if (open is null || !open.IsValidObject)
                    continue;

                try
                {
                    open.Close(false);
                }
                catch (Exception)
                {
                    // Уже закрыт или невалиден.
                }
            }
        }
    }

    private static Result Fail(string note) => new() { Seeded = false, Note = note };

    /// <summary>
    /// Тёзка в любой категории делает файловую загрузку неразрешимым
    /// конфликтом (молчаливый false); копия шаблона одноразовая — удаляем
    /// заранее, вне открытой транзакции.
    /// </summary>
    private static void RemoveConflictingFamilies(Document doc, string familyName)
    {
        List<ElementId> conflicts;
        try
        {
            conflicts = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Where(family => string.Equals(
                    family.Name, familyName, StringComparison.OrdinalIgnoreCase))
                .Select(family => family.Id)
                .ToList();
        }
        catch (Exception)
        {
            // LoadFamily ниже сообщит о конфликте ошибкой сида.
            return;
        }

        if (conflicts.Count == 0)
            return;

        try
        {
            using var transaction = new Transaction(doc, "Remove conflicting family '" + familyName + "'");
            transaction.Start();
            foreach (var id in conflicts)
                doc.Delete(id);
            transaction.Commit();
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }

    /// <summary>Nesting load of a shared family: FamilySource.Project keeps the
    /// shared behaviour (the nested instance is exposed as a subcomponent).</summary>
    private sealed class NestLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(
            Family sharedFamily,
            bool familyInUse,
            out FamilySource source,
            out bool overwriteParameterValues)
        {
            source = FamilySource.Project;
            overwriteParameterValues = true;
            return true;
        }
    }
}

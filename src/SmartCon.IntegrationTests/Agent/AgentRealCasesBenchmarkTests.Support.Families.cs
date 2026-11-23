using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Shared seed helpers of the real-cases benchmark v2 family cases (cases 12
/// and 13): generic model families with exact type names (FamilySeed pattern,
/// but WITHOUT extra parameters, so list_type_parameter_candidates and the
/// audit see a deterministic parameter landscape), the placement of one
/// instance per type and schedulable-field lookups for the schedule seeds.
/// Family loading runs OUTSIDE any project transaction (LoadFamily on a
/// modifiable document throws — verified API trap, AgentFillAuditToolsTests).
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // Ни одного Revit-типа в свойствах (rule 1 интеграционных тестов).
    private sealed class CaseGenericTypeSeed
    {
        public string TypeName { get; init; } = string.Empty;

        public long TypeId { get; set; }

        /// <summary>Один размещённый экземпляр типа (0 — не размещён).</summary>
        public long InstanceId { get; set; }
    }

    private sealed class CaseGenericFamilySeed
    {
        public string FamilyName { get; init; } = string.Empty;

        public List<CaseGenericTypeSeed> Types { get; } = new();

        public string RfaPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Создаёт generic-семейства с точными именами (семейство + типы) из
    /// family-шаблона установки. Имя семейства задаётся ЯВНО в документе
    /// семейства (OwnerFamily.Name в транзакции): имя файла SaveAs тоже
    /// назначает имя семейства, но temp-префиксы имён файлов утекали в него
    /// (живой прогон 2026-11-22: «case13_Вентустановка В-1»). Семейства чистые:
    /// без параметров, без формул. Возвращает null + причину, если шаблон
    /// недоступен или имя после загрузки не совпало (Skip.Test у вызывающего).
    /// </summary>
    private List<CaseGenericFamilySeed>? SeedCaseGenericFamilies(
        (string FamilyName, string[] TypeNames)[] specification,
        out string? skipReason)
    {
        skipReason = null;
        var template = SampleFiles.FindFamilyTemplate(Application);
        if (template is null)
        {
            skipReason = "В установленном Revit нет family-шаблона Generic Model (.rft)";
            return null;
        }

        // Уникальный подкаталог прогона: файлы зовутся точно как семейства,
        // без каких-либо префиксов (коллизий между прогонами нет).
        var directory = Path.Combine(
            Path.GetTempPath(), "SmartConCaseFamilies", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var families = new List<CaseGenericFamilySeed>(specification.Length);
            foreach (var item in specification)
        {
            // На базе реального шаблона тёзка с другим содержимым ломает загрузку
            // (Family loading failed / молчаливый false) — удаляем до загрузки.
            RemoveCaseConflictingFamilies(Doc, item.FamilyName);
            var rfaPath = Path.Combine(directory, item.FamilyName + ".rfa");
            Document? familyDoc = null;
            try
            {
                familyDoc = Application.NewFamilyDocument(template);
                if (familyDoc is null)
                {
                    skipReason = "Revit не создал семейство из шаблона " + template;
                    return null;
                }

                using (var transaction = new Transaction(familyDoc, "Seed case family"))
                {
                    transaction.Start();
                    var manager = familyDoc.FamilyManager;
                    if (manager.CurrentType is not null)
                        manager.RenameCurrentType(item.TypeNames[0]);
                    else
                        manager.NewType(item.TypeNames[0]);

                    foreach (var typeName in item.TypeNames.Skip(1))
                        manager.NewType(typeName);

                    familyDoc.OwnerFamily.Name = item.FamilyName;
                    if (transaction.Commit() != TransactionStatus.Committed)
                    {
                        skipReason = "Транзакция сида семейства «" + item.FamilyName +
                                     "» не завершилась";
                        return null;
                    }
                }

                familyDoc.SaveAs(rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
                var family = familyDoc.LoadFamily(Doc, new OverwriteFamilyLoadOptions());
                if (family is null)
                {
                    skipReason = "Revit не загрузил сид-семейство «" + item.FamilyName + "»";
                    return null;
                }

                if (!string.Equals(family.Name, item.FamilyName, StringComparison.OrdinalIgnoreCase))
                {
                    skipReason = "Семейство «" + item.FamilyName + "» загрузилось под именем «" +
                                 family.Name + "» — переименование в документе семейства не применилось";
                    return null;
                }

                var seed = new CaseGenericFamilySeed
                {
                    FamilyName = item.FamilyName,
                    RfaPath = rfaPath
                };
                foreach (var typeName in item.TypeNames)
                {
                    var type = FindCaseFamilySymbol(family, typeName);
                    if (type is null)
                    {
                        skipReason = "Тип «" + typeName + "» семейства «" + item.FamilyName +
                                     "» не найден после загрузки";
                        return null;
                    }

                    seed.Types.Add(new CaseGenericTypeSeed
                    {
                        TypeName = typeName,
                        TypeId = type.Id.GetValue()
                    });
                }

                families.Add(seed);
            }
            catch (Exception ex)
            {
                skipReason = "Не удалось сидировать семейство «" + item.FamilyName + "»: " +
                             ex.GetType().Name + ": " + ex.Message;
                return null;
            }
            finally
            {
                CloseCaseFamilyDoc(familyDoc);
            }
        }

        return families;
    }

    /// <summary>
    /// Удаляет из целевого документа уже загруженные семейства с тем же именем
    /// (в любой категории). На базе реального шаблона владельца тёзка с другим
    /// содержимым или категорией делает загрузку неразрешимым конфликтом:
    /// InvalidOperationException «Family loading failed» у Document-перегрузки
    /// LoadFamily и молчаливый false у строчной (Jeremy Tammik, «Reloading a
    /// Family»; Autodesk docs LoadFamily). Копия шаблона одноразовая, каскадное
    /// удаление экземпляров тёзки безопасно. Вызывается вне открытой транзакции.
    /// </summary>
    private static void RemoveCaseConflictingFamilies(Document doc, string familyName)
    {
        List<ElementId> conflicts;
        try
        {
            conflicts = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitFamily))
                .Cast<RevitFamily>()
                .Where(family => string.Equals(
                    family.Name, familyName, StringComparison.OrdinalIgnoreCase))
                .Select(family => family.Id)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine("PROBE case-families: template family '" + familyName +
                              "' not scanned: " + ex.GetType().Name + ": " + ex.Message);
            return;
        }

        if (conflicts.Count == 0)
            return;

        try
        {
            using var transaction = new Transaction(
                doc, "Seed case family conflict '" + familyName + "'");
            transaction.Start();
            foreach (var id in conflicts)
                doc.Delete(id);
            if (transaction.Commit() != TransactionStatus.Committed)
                Console.WriteLine("PROBE case-families: template family '" + familyName +
                                  "' not removed: commit rolled back");
        }
        catch (Exception ex)
        {
            Console.WriteLine("PROBE case-families: template family '" + familyName +
                              "' not removed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Свежая ExternalDefinition-обёртка из текущего ФОП по группе и имени.
    /// Обёртки, полученные при создании определений, могут протухнуть нативно
    /// после перезаписи/удаления семейства, несущего shared-параметры (Revit
    /// перечитывает файл общих параметров), при этом managed-IsValidObject всё
    /// ещё возвращает true, а BindingMap.Insert падает на definition.Visible
    /// (InvalidObjectExceptionProxy, точечный прогон 2026-11-23). Поэтому
    /// живость проверяется нативно (чтение Visible — того же свойства, что
    /// читает Insert), а не через IsValidObject; при обнаружении трупа ФОП
    /// перечитывается повторным присваиванием SharedParametersFilename и
    /// поиск повторяется.
    /// </summary>
    private ExternalDefinition? FindCaseSharedDefinition(
        string groupName, string parameterName)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            ExternalDefinition? found = null;
            try
            {
                var file = Application.OpenSharedParameterFile();
                if (file is not null)
                {
                    foreach (DefinitionGroup candidate in file.Groups)
                    {
                        if (!string.Equals(candidate.Name, groupName, StringComparison.Ordinal))
                            continue;

                        foreach (Definition definition in candidate.Definitions)
                        {
                            if (string.Equals(
                                    definition.Name, parameterName, StringComparison.Ordinal) &&
                                definition is ExternalDefinition external)
                            {
                                found = external;
                                break;
                            }
                        }

                        if (found is not null)
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // Обёртка файла протухла — перечитываем ФОП следующей попыткой.
            }

            if (found is not null)
            {
                try
                {
                    _ = found.Visible; // нативная проверка живости (см. док-комментарий)
                    return found;
                }
                catch (Exception)
                {
                    // Нативный труп — форсируем перечитывание ФОП.
                }
            }

            try
            {
                var path = Application.SharedParametersFilename;
                Application.SharedParametersFilename = path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Размещает по одному экземпляру каждого типа (сетка 8 колонок).</summary>
    private static void PlaceCaseGenericInstances(
        Document doc,
        IEnumerable<CaseGenericFamilySeed> families)
    {
        var placed = 0;
        foreach (var family in families)
        {
            foreach (var type in family.Types)
            {
                if (doc.GetElement(ElementIdCompat.Create(type.TypeId)) is not FamilySymbol symbol)
                    continue;

                symbol.Activate();
                var instance = doc.Create.NewFamilyInstance(
                    new XYZ((placed % 8) * 4.0, (placed / 8) * 4.0, 0.0),
                    symbol,
                    StructuralType.NonStructural);
                type.InstanceId = instance.Id.GetValue();
                placed++;
            }
        }
    }

    private static void DisposeCaseGenericFamilies(List<CaseGenericFamilySeed>? families)
    {
        if (families is null)
            return;

        foreach (var family in families)
        {
            try
            {
                if (File.Exists(family.RfaPath))
                    File.Delete(family.RfaPath);
            }
            catch (IOException)
            {
                // Temp file cleanup is best-effort.
            }
        }

        // Пустой GUID-подкаталог прогона удаляем тем же best-effort.
        try
        {
            var directory = Path.GetDirectoryName(families[0].RfaPath);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: false);
        }
        catch (Exception)
        {
            // Занят или не пуст — останется во %TEMP%, не критично.
        }
    }

    /// <summary>Поле по точному отображаемому имени (локальные варианты RU/EN).</summary>
    private static SchedulableField? FindCaseSchedulableFieldByName(
        Document doc,
        ScheduleDefinition definition,
        params string[] names)
    {
        foreach (var field in definition.GetSchedulableFields())
        {
            var name = field.GetName(doc);
            if (names.Any(candidate =>
                    string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)))
                return field;
        }

        return null;
    }

    /// <summary>
    /// Добавляет в спеку поле общего параметра по GUID определения — через поиск
    /// schedulable-поля (работает и для type-bound параметров, без угадывания
    /// ScheduleFieldType).
    /// </summary>
    private static bool TryAddCaseSharedField(
        Document doc,
        ScheduleDefinition definition,
        Guid guid)
    {
        var shared = SharedParameterElement.Lookup(doc, guid);
        if (shared is null)
            return false;

        foreach (var field in definition.GetSchedulableFields())
        {
            if (field.ParameterId == shared.Id)
            {
                definition.AddField(field);
                return true;
            }
        }

        return false;
    }

    private static FamilySymbol? FindCaseFamilySymbol(RevitFamily family, string typeName)
    {
        foreach (var symbolId in family.GetFamilySymbolIds())
        {
            if (family.Document.GetElement(symbolId) is FamilySymbol symbol &&
                string.Equals(symbol.Name, typeName, StringComparison.OrdinalIgnoreCase))
                return symbol;
        }

        return null;
    }

    private static void CloseCaseFamilyDoc(Document? familyDoc)
    {
        if (familyDoc is null || !familyDoc.IsValidObject)
            return;

        try
        {
            familyDoc.Close(false);
        }
        catch (Exception)
        {
            // Уже закрыт или невалиден — ничего не делать.
        }
    }
}

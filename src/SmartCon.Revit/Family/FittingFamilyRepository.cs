using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Compatibility;
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.Revit.Family;

/// <summary>
/// Получение семейств фитингов, подходящих для PipeConnect-маппинга.
/// Критерии: OST_PipeFitting + PartType=MultiPort + ровно 2 ConnectorElement.
/// EditFamily требует doc.IsModifiable == false (вызывать вне транзакции).
/// </summary>
public sealed class FittingFamilyRepository : IFittingFamilyRepository
{
    public IReadOnlyList<FamilyInfo> GetEligibleFittingFamilies(Document doc)
    {
        SmartConLogger.Info("=== НАЧАЛО GetEligibleFittingFamilies ===");
        SmartConLogger.Info($"Document.Title = {doc.Title}");
        SmartConLogger.Info($"Document.IsModifiable = {doc.IsModifiable}");

        if (doc.IsModifiable)
        {
            SmartConLogger.Info("ОШИБКА: doc.IsModifiable == true, выбрасываем исключение");
            throw new InvalidOperationException(
                "GetEligibleFittingFamilies должен вызываться вне транзакции (EditFamily требует IsModifiable=false).");
        }

        // ── Фаза 1: быстрый фильтр по PartType прямо в проекте (без EditFamily) ──────
        // Family в project doc тоже имеет параметр FAMILY_CONTENT_PART_TYPE.
        // get_Parameter — просто чтение из памяти, никаких файловых операций.
        var sw = Stopwatch.StartNew();

        var allPipeFitting = new FilteredElementCollector(doc)
            .OfClass(typeof(RevitFamily))
            .Cast<RevitFamily>()
            .Where(f => f.FamilyCategory?.Id.GetValue() == (long)BuiltInCategory.OST_PipeFitting)
            .ToList();

        SmartConLogger.Info($"Фаза 1: найдено {allPipeFitting.Count} семейств OST_PipeFitting");

        var multiPortFamilies = new List<RevitFamily>();
        foreach (var f in allPipeFitting)
        {
            var p = f.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
            if (p is null || !p.HasValue)
            {
                SmartConLogger.Info($"  '{f.Name}': FAMILY_CONTENT_PART_TYPE недоступен в проекте");
                continue;
            }
            var pt = (PartType)p.AsInteger();
            SmartConLogger.Info($"  '{f.Name}': PartType={pt}");
            if (pt == PartType.MultiPort)
                multiPortFamilies.Add(f);
        }

        SmartConLogger.Info($"Фаза 1 завершена за {sw.ElapsedMilliseconds} мс: {multiPortFamilies.Count} MultiPort семейств");

        // ── Фаза 2: EditFamily только для MultiPort → считаем ConnectorElement ──────
        sw.Restart();
        var result = new List<FamilyInfo>();
        int twoConnectorCount = 0;

        foreach (var family in multiPortFamilies)
        {
            Document? familyDoc = null;
            try
            {
                SmartConLogger.Info($"\nФаза 2, EditFamily: '{family.Name}'");
                familyDoc = doc.EditFamily(family);

                var connCount = new FilteredElementCollector(familyDoc)
                    .OfCategory(BuiltInCategory.OST_ConnectorElem)
                    .WhereElementIsNotElementType()
                    .Cast<ConnectorElement>()
                    .Count();

                SmartConLogger.Info($"  ConnectorElement count = {connCount}");

                if (connCount != 2)
                {
                    SmartConLogger.Info($"  connCount != 2, пропуск");
                    continue;
                }
                twoConnectorCount++;

                var symbolNames = family.GetFamilySymbolIds()
                    .Select(id => doc.GetElement(id) as FamilySymbol)
                    .Where(s => s is not null)
                    .Select(s => s!.Name)
                    .OrderBy(n => n)
                    .ToList();

                result.Add(new FamilyInfo(
                    FamilyName: family.Name,
                    PartTypeName: "MultiPort",
                    ConnectorCount: connCount,
                    SymbolNames: symbolNames
                ));

                SmartConLogger.Info($"  >>> ДОБАВЛЕНО: {family.Name} ({symbolNames.Count} типоразмеров)");
            }
            catch (Exception ex)
            {
                SmartConLogger.Info($"  ОШИБКА '{family.Name}': {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                familyDoc?.Close(false);
            }
        }

        SmartConLogger.Info($"\n=== ИТОГ ===");
        SmartConLogger.Info($"Всего OST_PipeFitting: {allPipeFitting.Count}");
        SmartConLogger.Info($"MultiPort (без EditFamily): {multiPortFamilies.Count}");
        SmartConLogger.Info($"EditFamily вызовов: {multiPortFamilies.Count} (вместо {allPipeFitting.Count})");
        SmartConLogger.Info($"С 2 коннекторами: {twoConnectorCount}");
        SmartConLogger.Info($"Результат: {result.Count}");
        SmartConLogger.Info($"Фаза 2 заняла: {sw.ElapsedMilliseconds} мс");

        return result;
    }
}

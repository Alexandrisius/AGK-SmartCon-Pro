using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.Revit.Family;

/// <summary>
/// Получение семейств фитингов, подходящих для PipeConnect-маппинга.
/// Критерии: OST_PipeFitting + PartType=MultiPort + ровно 2 ConnectorElement.
/// EditFamily требует doc.IsModifiable == false (вызывать вне транзакции).
/// </summary>
public sealed class FittingFamilyRepository : IFittingFamilyRepository
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AGK", "SmartCon", "FittingFamilyRepository.log");

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Debug.WriteLine(line);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* игнорируем ошибки логирования */ }
    }

    public IReadOnlyList<FamilyInfo> GetEligibleFittingFamilies(Document doc)
    {
        Log("=== НАЧАЛО GetEligibleFittingFamilies ===");
        Log($"Document.Title = {doc.Title}");
        Log($"Document.IsModifiable = {doc.IsModifiable}");

        if (doc.IsModifiable)
        {
            Log("ОШИБКА: doc.IsModifiable == true, выбрасываем исключение");
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
            .Where(f => f.FamilyCategory?.Id.Value == (long)BuiltInCategory.OST_PipeFitting)
            .ToList();

        Log($"Фаза 1: найдено {allPipeFitting.Count} семейств OST_PipeFitting");

        var multiPortFamilies = new List<RevitFamily>();
        foreach (var f in allPipeFitting)
        {
            var p = f.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
            if (p is null || !p.HasValue)
            {
                Log($"  '{f.Name}': FAMILY_CONTENT_PART_TYPE недоступен в проекте");
                continue;
            }
            var pt = (PartType)p.AsInteger();
            Log($"  '{f.Name}': PartType={pt}");
            if (pt == PartType.MultiPort)
                multiPortFamilies.Add(f);
        }

        Log($"Фаза 1 завершена за {sw.ElapsedMilliseconds} мс: {multiPortFamilies.Count} MultiPort семейств");

        // ── Фаза 2: EditFamily только для MultiPort → считаем ConnectorElement ──────
        sw.Restart();
        var result = new List<FamilyInfo>();
        int twoConnectorCount = 0;

        foreach (var family in multiPortFamilies)
        {
            Document? familyDoc = null;
            try
            {
                Log($"\nФаза 2, EditFamily: '{family.Name}'");
                familyDoc = doc.EditFamily(family);

                var connCount = new FilteredElementCollector(familyDoc)
                    .OfCategory(BuiltInCategory.OST_ConnectorElem)
                    .WhereElementIsNotElementType()
                    .Cast<ConnectorElement>()
                    .Count();

                Log($"  ConnectorElement count = {connCount}");

                if (connCount != 2)
                {
                    Log($"  connCount != 2, пропуск");
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

                Log($"  >>> ДОБАВЛЕНО: {family.Name} ({symbolNames.Count} типоразмеров)");
            }
            catch (Exception ex)
            {
                Log($"  ОШИБКА '{family.Name}': {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                familyDoc?.Close(false);
            }
        }

        Log($"\n=== ИТОГ ===");
        Log($"Всего OST_PipeFitting: {allPipeFitting.Count}");
        Log($"MultiPort (без EditFamily): {multiPortFamilies.Count}");
        Log($"EditFamily вызовов: {multiPortFamilies.Count} (вместо {allPipeFitting.Count})");
        Log($"С 2 коннекторами: {twoConnectorCount}");
        Log($"Результат: {result.Count}");
        Log($"Фаза 2 заняла: {sw.ElapsedMilliseconds} мс | Лог: {LogPath}");

        return result;
    }
}

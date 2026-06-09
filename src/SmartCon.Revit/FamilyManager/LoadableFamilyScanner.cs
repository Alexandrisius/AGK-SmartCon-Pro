using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

public sealed class LoadableFamilyScanner : ILoadableFamilyScanner
{
    public IReadOnlyList<LoadableFamilyInfo> GetUniqueFamilies(Document activeDoc)
    {
        if (activeDoc is null) return [];

        var uniqueFamilies = new FilteredElementCollector(activeDoc)
            .OfClass(typeof(FamilyInstance))
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>()
            .Where(fi => fi.IsValidObject && fi.Symbol?.Family is not null && !fi.Symbol.Family.IsInPlace)
            .GroupBy(fi => fi.Symbol.Family.UniqueId)
            .Select(g =>
            {
                var family = g.First().Symbol.Family;
                return new LoadableFamilyInfo(
                    FamilyName: family.Name,
                    FamilyUniqueId: family.UniqueId,
                    CategoryName: family.FamilyCategory?.Name ?? "Unknown",
                    TypeCount: family.GetFamilySymbolIds().Count);
            })
            .OrderBy(i => i.FamilyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var _scope = SmartConLogger.BeginScope("LoadableFamilyScanner",
            ("Count", uniqueFamilies.Count));
        SmartConLogger.Debug($"Scanned {uniqueFamilies.Count} placed loadable family group(s)");

        return uniqueFamilies;
    }
}

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

        // #196 (ADR-027 §"Not supported"): non-editable families — curtain
        // wall system panels («Системная панель», OST_CurtainWallPanels) —
        // cannot be opened via Document.EditFamily (Revit API throws
        // ArgumentException "This family is not editable"), so the loadable
        // import path is fundamentally inapplicable to them. Excluding them
        // here keeps the batch dialog free of rows that would always fail.
        var families = new FilteredElementCollector(activeDoc)
            .OfClass(typeof(FamilyInstance))
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>()
            .Where(fi => fi.IsValidObject && fi.Symbol?.Family is not null && !fi.Symbol.Family.IsInPlace)
            .Select(fi => fi.Symbol!.Family)
            .GroupBy(f => f.UniqueId)
            .Select(g => g.First())
            .ToList();

        var skippedNonEditable = 0;
        var uniqueFamilies = new List<LoadableFamilyInfo>(families.Count);
        foreach (var family in families)
        {
            if (!family.IsEditable)
            {
                skippedNonEditable++;
                continue;
            }

            uniqueFamilies.Add(new LoadableFamilyInfo(
                FamilyName: family.Name,
                FamilyUniqueId: family.UniqueId,
                CategoryName: family.FamilyCategory?.Name ?? "Unknown",
                TypeCount: family.GetFamilySymbolIds().Count));
        }

        uniqueFamilies.Sort((a, b) => string.Compare(a.FamilyName, b.FamilyName, StringComparison.OrdinalIgnoreCase));

        using var _scope = SmartConLogger.BeginScope("LoadableFamilyScanner",
            ("Count", uniqueFamilies.Count));
        SmartConLogger.Debug($"Scanned {uniqueFamilies.Count} placed loadable family group(s)");
        if (skippedNonEditable > 0)
        {
            SmartConLogger.Info(
                $"Skipped {skippedNonEditable} non-editable family group(s) " +
                "(system-only content such as curtain wall panels cannot be edited — excluded from loadable import, see #196)");
        }

        return uniqueFamilies;
    }
}

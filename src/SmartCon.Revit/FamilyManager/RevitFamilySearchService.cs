using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Реализация IFamilySearchService через Revit API.
/// Все методы вызываются только из ExternalEvent handler (I-01).
/// </summary>
public sealed class RevitFamilySearchService : IFamilySearchService
{
    private readonly IRevitContext _revitContext;

    public RevitFamilySearchService(IRevitContext revitContext)
    {
        _revitContext = revitContext;
    }

    public bool IsFamilyLoaded(string familyName)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null) return false;

        var family = FindByName(doc, familyName);

        if (family is not null)
            SmartConLogger.Info($"IsFamilyLoaded('{familyName}'): FOUND — {DescribeFamily(family)}");
        else
            SmartConLogger.Info($"IsFamilyLoaded('{familyName}'): not found");

        return family is not null;
    }

    public IReadOnlyList<string> GetFamilyTypeNames(string familyName)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null) return Array.Empty<string>();

        var family = new FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.Family))
            .Cast<Autodesk.Revit.DB.Family>()
            .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));

        if (family is null) return Array.Empty<string>();

        return family.GetFamilySymbolIds()
            .Select(id => doc.GetElement(id))
            .OfType<FamilySymbol>()
            .Select(s => s.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool HasFamilyType(string familyName, string typeName)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null) return false;

        var family = FindByName(doc, familyName);

        if (family is null)
        {
            SmartConLogger.Info($"HasFamilyType('{familyName}', '{typeName}'): family not found");
            return false;
        }

        var found = family.GetFamilySymbolIds()
            .Select(id => doc.GetElement(id))
            .OfType<FamilySymbol>()
            .Any(s => s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));

        SmartConLogger.Info($"HasFamilyType('{familyName}', '{typeName}'): {found} — {DescribeFamily(family)}");
        return found;
    }

    public IReadOnlyCollection<string> GetAllLoadedFamilyNames()
    {
        var doc = _revitContext.GetDocument();
        if (doc is null) return Array.Empty<string>();

        return new FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.Family))
            .Cast<Autodesk.Revit.DB.Family>()
            .Select(f => f.Name)
            .ToHashSet();
    }

    private static Autodesk.Revit.DB.Family? FindByName(Document doc, string familyName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.Family))
            .Cast<Autodesk.Revit.DB.Family>()
            .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));
    }

    internal static string DescribeFamily(Autodesk.Revit.DB.Family family)
        => $"Family(Name='{family.Name}', Id={family.Id}, UniqueId={family.UniqueId})";
}

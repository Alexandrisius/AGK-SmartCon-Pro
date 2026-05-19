using Autodesk.Revit.DB;
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

        var family = new FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.Family))
            .Cast<Autodesk.Revit.DB.Family>()
            .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));

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

        var family = new FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.Family))
            .Cast<Autodesk.Revit.DB.Family>()
            .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));

        if (family is null) return false;

        return family.GetFamilySymbolIds()
            .Select(id => doc.GetElement(id))
            .OfType<FamilySymbol>()
            .Any(s => s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
    }
}

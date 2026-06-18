using Autodesk.Revit.DB;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of <see cref="IFamilyFinder"/>. Uses <c>FilteredElementCollector</c>
/// with <c>OfClass(Family)</c> to enumerate loaded families and match by name.
/// </summary>
public sealed class RevitFamilyFinder : IFamilyFinder
{
    public ElementId? FindByName(Document doc, string familyName)
    {
        if (doc is null || string.IsNullOrEmpty(familyName)) return null;
        using var collector = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Family));
        foreach (Autodesk.Revit.DB.Family f in collector)
        {
            if (string.Equals(f.Name, familyName, StringComparison.Ordinal)) return f.Id;
        }
        return null;
    }
}

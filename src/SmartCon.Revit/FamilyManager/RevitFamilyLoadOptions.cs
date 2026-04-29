using Autodesk.Revit.DB;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Реализация IFamilyLoadOptions, которая разрешает загрузку семейства
/// с обновлением версии и перезаписью существующих параметров.
/// </summary>
public sealed class RevitFamilyLoadOptions : IFamilyLoadOptions
{
    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    {
        overwriteParameterValues = true;
        return true;
    }

    public bool OnSharedFamilyFound(Autodesk.Revit.DB.Family sharedFamily, bool familyInUse, out Autodesk.Revit.DB.FamilySource source, out bool overwriteParameterValues)
    {
        source = Autodesk.Revit.DB.FamilySource.Family;
        overwriteParameterValues = true;
        return true;
    }
}

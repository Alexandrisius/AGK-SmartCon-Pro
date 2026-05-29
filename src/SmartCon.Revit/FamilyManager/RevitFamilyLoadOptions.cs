using Autodesk.Revit.DB;
using SmartCon.Core.Logging;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Реализация IFamilyLoadOptions, которая разрешает загрузку семейства
/// с обновлением версии и перезаписью существующих параметров.
/// </summary>
public sealed class RevitFamilyLoadOptions : IFamilyLoadOptions
{
    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    {
        SmartConLogger.Info($"[FamilyLoadOptions] OnFamilyFound called: familyInUse={familyInUse}");
        overwriteParameterValues = true;
        return true;
    }

    public bool OnSharedFamilyFound(Autodesk.Revit.DB.Family sharedFamily, bool familyInUse, out Autodesk.Revit.DB.FamilySource source, out bool overwriteParameterValues)
    {
        SmartConLogger.Info($"[FamilyLoadOptions] OnSharedFamilyFound called: sharedFamily='{sharedFamily.Name}', familyInUse={familyInUse}");
        source = Autodesk.Revit.DB.FamilySource.Family;
        overwriteParameterValues = true;
        return true;
    }
}

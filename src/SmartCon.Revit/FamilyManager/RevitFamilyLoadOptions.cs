using Autodesk.Revit.DB;
using SmartCon.Core.Logging;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Реализация IFamilyLoadOptions, которая разрешает загрузку семейства
/// с обновлением версии и настраиваемой перезаписью параметров.
/// </summary>
public sealed class RevitFamilyLoadOptions : IFamilyLoadOptions
{
    private readonly bool _overwriteParameterValues;

    public RevitFamilyLoadOptions(bool overwriteParameterValues = true)
    {
        _overwriteParameterValues = overwriteParameterValues;
    }

    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    {
        SmartConLogger.Info($"[FamilyLoadOptions] OnFamilyFound called: familyInUse={familyInUse}, overwrite={_overwriteParameterValues}");
        overwriteParameterValues = _overwriteParameterValues;
        return true;
    }

    public bool OnSharedFamilyFound(Autodesk.Revit.DB.Family sharedFamily, bool familyInUse, out Autodesk.Revit.DB.FamilySource source, out bool overwriteParameterValues)
    {
        var familyName = sharedFamily?.Name ?? "<null>";
        SmartConLogger.Info($"[FamilyLoadOptions] OnSharedFamilyFound called: sharedFamily='{familyName}', familyInUse={familyInUse}, overwrite={_overwriteParameterValues}");
        source = Autodesk.Revit.DB.FamilySource.Family;
        overwriteParameterValues = _overwriteParameterValues;
        return true;
    }
}

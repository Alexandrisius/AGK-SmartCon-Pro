using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

public sealed class RevitFileInfoReader : IRevitFileInfoReader
{
    public int? ReadRevitVersion(string filePath)
    {
        try
        {
            using var info = BasicFileInfo.Extract(filePath);
            var format = info?.Format;
            if (int.TryParse(format, out var v))
            {
                SmartConLogger.Info($"[FileInfoReader] Detected Revit version {v} from file: {System.IO.Path.GetFileName(filePath)}");
                return v;
            }

            SmartConLogger.Info($"[FileInfoReader] Could not parse Format '{format}' from file: {System.IO.Path.GetFileName(filePath)}");
            return null;
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"[FileInfoReader] BasicFileInfo.Extract failed for '{System.IO.Path.GetFileName(filePath)}': {ex.Message}");
            return null;
        }
    }
}

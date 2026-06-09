using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

public sealed class RevitFileInfoReader : IRevitFileInfoReader
{
    public int? ReadRevitVersion(string filePath)
    {
        using var _scope = SmartConLogger.BeginScope("FileInfoReader",
            ("Method", "ReadRevitVersion"),
            ("FilePath", System.IO.Path.GetFileName(filePath)));
        try
        {
            using var info = BasicFileInfo.Extract(filePath);
            var format = info?.Format;
            if (int.TryParse(format, out var v))
            {
                SmartConLogger.Info($"Detected Revit version {v}");
                return v;
            }

            SmartConLogger.Info($"Could not parse Format '{format}'");
            return null;
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"BasicFileInfo.Extract failed: {ex.Message}");
            return null;
        }
    }
}


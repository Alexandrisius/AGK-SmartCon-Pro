using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

public interface IShareProjectSettingsRepository
{
    ShareProjectSettings Load(Document doc);
    void Save(Document doc, ShareProjectSettings settings);
    string ExportToJson(ShareProjectSettings settings);
    ShareProjectSettings ImportFromJson(string json);
    ExportNameOverride? LoadExportNameOverride(Document doc);
    void SaveExportNameOverride(Document doc, ExportNameOverride overrideData);
}

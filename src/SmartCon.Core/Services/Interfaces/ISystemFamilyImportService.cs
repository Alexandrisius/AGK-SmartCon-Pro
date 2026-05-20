using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface ISystemFamilyImportService
{
    SystemFamilyImportResult ImportFromSelection();
}

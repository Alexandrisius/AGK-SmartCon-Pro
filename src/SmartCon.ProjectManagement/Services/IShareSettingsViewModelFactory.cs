using Autodesk.Revit.DB;
using SmartCon.ProjectManagement.ViewModels;

namespace SmartCon.ProjectManagement.Services;

public interface IShareSettingsViewModelFactory
{
    ShareSettingsViewModel Create(Document doc);
}

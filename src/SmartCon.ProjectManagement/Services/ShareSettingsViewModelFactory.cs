using Autodesk.Revit.DB;
using SmartCon.Core.Services.Interfaces;
using SmartCon.ProjectManagement.ViewModels;

namespace SmartCon.ProjectManagement.Services;

public sealed class ShareSettingsViewModelFactory(
    IShareProjectSettingsRepository settingsRepository,
    IViewRepository viewRepository,
    IFileNameParser fileNameParser) : IShareSettingsViewModelFactory
{
    public ShareSettingsViewModel Create(Document doc)
    {
        return new ShareSettingsViewModel(settingsRepository, viewRepository, fileNameParser, doc);
    }
}

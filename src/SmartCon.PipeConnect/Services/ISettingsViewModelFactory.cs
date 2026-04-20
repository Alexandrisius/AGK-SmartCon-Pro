using Autodesk.Revit.DB;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Services;

public interface ISettingsViewModelFactory
{
    MappingEditorViewModel Create(Document doc);
}

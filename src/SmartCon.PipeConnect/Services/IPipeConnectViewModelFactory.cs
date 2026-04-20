using Autodesk.Revit.DB;
using SmartCon.Core.Models;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Services;

public interface IPipeConnectViewModelFactory
{
    PipeConnectSessionBuilder CreateSessionBuilder();

    PipeConnectEditorViewModel CreateEditorViewModel(PipeConnectSessionContext sessionCtx, Document doc);
}

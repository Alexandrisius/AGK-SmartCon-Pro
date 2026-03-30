using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.PipeConnect.Views;

namespace SmartCon.PipeConnect.Commands;

/// <summary>
/// Команда открытия окна настроек SmartCon (маппинг фитингов, типы коннекторов).
/// Phase 3B: открывает немодальное MappingEditorView.
/// IExternalCommand — уже на Revit main thread, Show() допустим напрямую.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var contextWriter = ServiceHost.GetService<IRevitContextWriter>();
            contextWriter.SetContext(commandData.Application);

            var revitContext = ServiceHost.GetService<IRevitContext>();
            var mappingRepo  = ServiceHost.GetService<IFittingMappingRepository>();

            var doc = revitContext.GetDocument();

            var familyNames = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Select(f => f.Name)
                .OrderBy(n => n)
                .ToList();

            var vm   = new MappingEditorViewModel(mappingRepo, familyNames);
            var view = new MappingEditorView(vm);
            view.Show();

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}

using System.Windows.Interop;
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
/// ADR-012: окно открывается модально (<c>ShowDialog</c>) — все Save-операции
/// выполняются через <c>ITransactionService</c> на Revit main thread без необходимости
/// в <c>IExternalEventHandler</c>, потому что модальный цикл удерживает вызов
/// внутри <c>Execute</c>.
/// Owner = главное окно Revit, чтобы окно не терялось поверх Revit-проекта.
/// TransactionMode.Manual — без активной транзакции, EditFamily разрешён.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var contextWriter = ServiceHost.GetService<IRevitContextWriter>();
            contextWriter.SetContext(commandData.Application);

            var revitContext = ServiceHost.GetService<IRevitContext>();
            var mappingRepo = ServiceHost.GetService<IFittingMappingRepository>();
            var familyRepo = ServiceHost.GetService<IFittingFamilyRepository>();
            var dialogService = ServiceHost.GetService<IDialogService>();

            var doc = revitContext.GetDocument();

            var eligibleFamilies = familyRepo.GetEligibleFittingFamilies(doc);
            var familyNames = eligibleFamilies.Select(f => f.FamilyName).ToList();

            var vm = new MappingEditorViewModel(mappingRepo, familyNames, dialogService);
            var view = new MappingEditorView(vm);
            new WindowInteropHelper(view).Owner = commandData.Application.MainWindowHandle;
            view.ShowDialog();

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}

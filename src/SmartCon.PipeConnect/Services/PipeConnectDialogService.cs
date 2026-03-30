using Autodesk.Revit.UI;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.PipeConnect.Views;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// Реализация IDialogService для PipeConnect-слоя.
/// Открывает WPF-окна. Должна вызываться с Revit main thread (STA).
/// </summary>
public sealed class PipeConnectDialogService : IDialogService
{
    public ConnectorTypeDefinition? ShowMiniTypeSelector(
        IReadOnlyList<ConnectorTypeDefinition> availableTypes)
    {
        var vm   = new MiniTypeSelectorViewModel(availableTypes);
        var view = new MiniTypeSelectorView(vm);
        view.ShowDialog();
        return vm.SelectedType;
    }

    public void ShowWarning(string title, string message)
    {
        TaskDialog.Show(title, message);
    }
}

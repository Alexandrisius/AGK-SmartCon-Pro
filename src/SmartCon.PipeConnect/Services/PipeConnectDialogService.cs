using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// WPF implementation of <see cref="IDialogService"/> for the PipeConnect module.
/// Delegates View creation to an <see cref="IDialogPresenter"/> registered at the
/// composition root, keeping this service decoupled from concrete View types.
/// </summary>
public sealed class PipeConnectDialogService(IDialogPresenter presenter) : IDialogService
{
    public ConnectorTypeDefinition? ShowMiniTypeSelector(
        IReadOnlyList<ConnectorTypeDefinition> availableTypes)
    {
        var vm = new MiniTypeSelectorViewModel(availableTypes);
        presenter.ShowDialog(vm);
        return vm.SelectedType;
    }

    public void ShowWarning(string title, string message)
    {
        TaskDialog.Show(title, message);
    }

    public IReadOnlyList<FittingMapping>? ShowFamilySelector(
        IReadOnlyList<string> availableFamilies,
        IReadOnlyList<FittingMapping> currentSelection)
    {
        var vm = new FamilySelectorViewModel(availableFamilies, currentSelection);
        presenter.ShowDialog(vm);
        return vm.GetResult();
    }

    public bool ShowFittingCtcSetup(
        string familyName,
        string symbolName,
        IReadOnlyList<IFittingCtcSetupItem> connectors,
        IReadOnlyList<ConnectorTypeDefinition> availableTypes)
    {
        // ViewModel expects the WPF-bindable implementation; callers in the
        // PipeConnect layer already pass concrete FittingCtcSetupItem instances.
        var items = connectors.Cast<FittingCtcSetupItem>().ToList();
        var vm = new FittingCtcSetupViewModel(familyName, symbolName, items, availableTypes);
        presenter.ShowDialog(vm);
        return vm.IsValid;
    }

    public string? ShowOpenJsonDialog(string title, string? initialDirectory = null, string? preselectFileName = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
            if (!string.IsNullOrWhiteSpace(preselectFileName))
            {
                var preselectPath = Path.Combine(initialDirectory, preselectFileName);
                if (File.Exists(preselectPath))
                    dialog.FileName = preselectPath;
            }
        }

        var result = dialog.ShowDialog();
        return result == true ? dialog.FileName : null;
    }

    public string? ShowSaveJsonDialog(string title, string? defaultFileName = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = "JSON files (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (!string.IsNullOrWhiteSpace(defaultFileName))
            dialog.FileName = defaultFileName;

        var result = dialog.ShowDialog();
        return result == true ? dialog.FileName : null;
    }
}

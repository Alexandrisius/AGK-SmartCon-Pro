using System.IO;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.PipeConnect.Views;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// WPF implementation of <see cref="IDialogService"/> for PipeConnect module.
/// Shows modal dialogs for connector type assignment, family selection, and warnings.
/// </summary>
public sealed class PipeConnectDialogService : IDialogService
{
    public ConnectorTypeDefinition? ShowMiniTypeSelector(
        IReadOnlyList<ConnectorTypeDefinition> availableTypes)
    {
        var vm = new MiniTypeSelectorViewModel(availableTypes);
        var view = new MiniTypeSelectorView(vm);
        view.ShowDialog();
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
        var view = new FamilySelectorView(vm);
        view.ShowDialog();
        return vm.GetResult();
    }

    public bool ShowFittingCtcSetup(
        string familyName,
        string symbolName,
        List<FittingCtcSetupItem> connectors,
        IReadOnlyList<ConnectorTypeDefinition> availableTypes)
    {
        var vm = new FittingCtcSetupViewModel(familyName, symbolName, connectors, availableTypes);
        var view = new FittingCtcSetupView(vm);
        view.ShowDialog();
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

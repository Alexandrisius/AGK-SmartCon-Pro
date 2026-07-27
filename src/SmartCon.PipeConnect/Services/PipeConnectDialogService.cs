using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
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
        Autodesk.Revit.UI.TaskDialog.Show(title, message);
    }

    public IReadOnlyList<FittingMapping>? ShowFamilySelector(
        IReadOnlyList<string> availableFamilies,
        IReadOnlyList<FittingMapping> currentSelection)
    {
        var vm = new FamilySelectorViewModel(availableFamilies, currentSelection);
        presenter.ShowDialog(vm);
        return vm.GetResult();
    }

    public string? ShowOpenJsonDialog(string title, string? initialDirectory = null, string? preselectFileName = null)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
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
        var dialog = new Microsoft.Win32.SaveFileDialog
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

    public void ShowError(string title, string message)
    {
        System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }

    public bool ShowQuestion(string title, string message)
    {
        var result = System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        return result == System.Windows.MessageBoxResult.Yes;
    }

    public UnconnectedChainChoice ShowUnconnectedChainWarning(int connectedCount, int totalCount)
    {
        var dialog = new Autodesk.Revit.UI.TaskDialog(
            LocalizationService.GetString("Dialog_UnconnectedChain_Title"))
        {
            MainInstruction = LocalizationService.GetString("Dialog_UnconnectedChain_Instruction"),
            MainContent = string.Format(
                LocalizationService.GetString("Dialog_UnconnectedChain_Message"),
                connectedCount, totalCount),
            CommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons.Cancel,
        };

        dialog.AddCommandLink(
            Autodesk.Revit.UI.TaskDialogCommandLinkId.CommandLink1,
            LocalizationService.GetString("Dialog_UnconnectedChain_ConnectAll"),
            LocalizationService.GetString("Dialog_UnconnectedChain_ConnectAllHint"));
        dialog.AddCommandLink(
            Autodesk.Revit.UI.TaskDialogCommandLinkId.CommandLink2,
            LocalizationService.GetString("Dialog_UnconnectedChain_ConnectAsIs"),
            LocalizationService.GetString("Dialog_UnconnectedChain_ConnectAsIsHint"));

        var result = dialog.Show();
        return result switch
        {
            Autodesk.Revit.UI.TaskDialogResult.CommandLink1 => UnconnectedChainChoice.ConnectAll,
            Autodesk.Revit.UI.TaskDialogResult.CommandLink2 => UnconnectedChainChoice.ConnectAsIs,
            _ => UnconnectedChainChoice.GoBack,
        };
    }

    public string? ShowFolderBrowser(string description, string? selectedPath = null)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
#if !NETFRAMEWORK
            UseDescriptionForTitle = true,
#endif
        };

        if (!string.IsNullOrWhiteSpace(selectedPath) && Directory.Exists(selectedPath))
            dialog.SelectedPath = selectedPath;

        var result = dialog.ShowDialog();
        return result == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }
}

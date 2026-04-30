using System.IO;
using System.Windows.Forms;
using Autodesk.Revit.UI;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

public sealed class FamilyManagerDialogService : IFamilyManagerDialogService
{
    private readonly IDialogPresenter _presenter;

    public FamilyManagerDialogService(IDialogPresenter presenter)
    {
        _presenter = presenter;
    }

    public string? ShowOpenFileDialog(string title, string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog();
        dialog.Title = title;
        dialog.Filter = "Revit Family Files (*.rfa)|*.rfa|All Files (*.*)|*.*";
        if (initialDirectory is not null) dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ShowImportDialog(string title, string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog();
        dialog.Title = title;
        dialog.Filter = "Revit Family Files (*.rfa)|*.rfa";
        if (initialDirectory is not null) dialog.InitialDirectory = initialDirectory;
        dialog.ValidateNames = false;
        dialog.CheckFileExists = false;
        dialog.CheckPathExists = true;
        dialog.FileName = "[Folder]";

        if (dialog.ShowDialog() != true) return null;

        var path = dialog.FileName;

        if (File.Exists(path))
            return path;

        if (path.EndsWith("[Folder]") || path.EndsWith("[Folder].rfa"))
        {
            var dir = path.Substring(0, path.IndexOf("[Folder]")).TrimEnd('\\');
            if (Directory.Exists(dir))
                return dir;
        }

        if (Directory.Exists(path))
            return path;

        var parentDir = Path.GetDirectoryName(path);
        if (parentDir is not null && Directory.Exists(parentDir))
            return parentDir;

        return path;
    }

    public string? ShowFolderBrowserDialog(string title, string? initialDirectory = null)
    {
        using var dialog = new FolderBrowserDialog();
        dialog.Description = title;
        if (initialDirectory is not null) dialog.SelectedPath = initialDirectory;
        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }

    public void ShowWarning(string title, string message) => Autodesk.Revit.UI.TaskDialog.Show(title, message);

    public void ShowError(string title, string message) => Autodesk.Revit.UI.TaskDialog.Show(title, message);

    public bool? ShowMetadataEdit(object viewModel) => _presenter.ShowDialog(viewModel);

    public string? ShowInputDialog(string title, string prompt, string defaultText = "")
    {
        var vm = new ViewModels.InputDialogViewModel
        {
            Title = title,
            Prompt = prompt,
            InputText = defaultText
        };
        var view = new Views.InputDialogView(vm);
        var result = view.ShowDialog();
        return result == true ? vm.InputText : null;
    }

    public bool ShowConfirmation(string title, string message)
    {
        var td = new Autodesk.Revit.UI.TaskDialog(title);
        td.MainContent = message;
        td.CommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons.Yes | Autodesk.Revit.UI.TaskDialogCommonButtons.No;
        return td.Show() == Autodesk.Revit.UI.TaskDialogResult.Yes;
    }
}

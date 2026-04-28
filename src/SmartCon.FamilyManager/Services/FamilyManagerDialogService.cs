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
        using var dialog = new OpenFileDialog();
        dialog.Title = title;
        dialog.Filter = "Revit Family Files (*.rfa)|*.rfa|All Files (*.*)|*.*";
        if (initialDirectory is not null) dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
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
}

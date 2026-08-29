using System.IO;
using System.Windows;
using System.Windows.Forms;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

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

    public string[]? ShowImportFilesDialog(string title, string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog();
        dialog.Title = title;
        dialog.Filter = "Revit Family Files (*.rfa)|*.rfa";
        if (initialDirectory is not null) dialog.InitialDirectory = initialDirectory;
        dialog.Multiselect = true;
        dialog.CheckFileExists = true;

        if (dialog.ShowDialog() != true) return null;
        return dialog.FileNames;
    }

    public string? ShowFolderBrowserDialog(string title, string? initialDirectory = null)
    {
        using var dialog = new FolderBrowserDialog();
        dialog.Description = title;
        if (initialDirectory is not null) dialog.SelectedPath = initialDirectory;
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    public void ShowWarning(string title, string message) =>
        System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowError(string title, string message) =>
        System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public string? ShowInputDialog(string title, string prompt, string defaultText = "", string placeholderText = "")
    {
        var vm = new ViewModels.InputDialogViewModel
        {
            Title = title,
            Prompt = prompt,
            InputText = defaultText,
            PlaceholderText = placeholderText
        };
        var view = new Views.InputDialogView(vm);
        var result = view.ShowDialog();
        return result == true ? vm.InputText : null;
    }

    public bool ShowConfirmation(string title, string message)
    {
        var vm = new ViewModels.ConfirmationDialogViewModel
        {
            Title = title,
            Message = message
        };
        var view = new Views.ConfirmationDialogView(vm);
        return view.ShowDialog() == true;
    }

    /// <inheritdoc/>
    public void ShowInfo(string title, string message)
    {
        var vm = new ViewModels.ConfirmationDialogViewModel
        {
            Title = title,
            Message = message,
            IsOkOnly = true,
            YesText = LanguageManager.GetString(StringLocalization.Keys.Btn_OK) ?? "OK",
        };
        var view = new Views.ConfirmationDialogView(vm);
        view.ShowDialog();
    }

    public global::SmartCon.Core.Services.Interfaces.DialogResult ShowYesNoCancel(string title, string message)
    {
        var result = System.Windows.MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result switch
        {
            MessageBoxResult.Yes => Core.Services.Interfaces.DialogResult.Yes,
            MessageBoxResult.No => Core.Services.Interfaces.DialogResult.No,
            MessageBoxResult.Cancel => Core.Services.Interfaces.DialogResult.Cancel,
            _ => Core.Services.Interfaces.DialogResult.None
        };
    }

    public bool? ShowCategoryTreeEditor(object viewModel) => _presenter.ShowDialog(viewModel);

    public bool? ShowProjectBaseRulesEditor(object viewModel) => _presenter.ShowDialog(viewModel);

    public bool? ShowParseRuleEditor(object viewModel) => _presenter.ShowDialog(viewModel);

    public bool? ShowFieldLibrary(object viewModel) => _presenter.ShowDialog(viewModel);

    public bool? ShowAllowedValues(object viewModel) => _presenter.ShowDialog(viewModel);

    public string? ShowCategoryPicker(object viewModel)
    {
        var result = _presenter.ShowDialog(viewModel);
        if (result != true) return null;
        if (viewModel is ViewModels.CategoryPickerViewModel pickerVm)
            return pickerVm.SelectedCategoryId ?? "";
        return null;
    }

    public string? ShowOpenJsonDialog(string title, string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog();
        dialog.Title = title;
        dialog.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*";
        dialog.CheckFileExists = true;
        dialog.CheckPathExists = true;
        if (!string.IsNullOrWhiteSpace(initialDirectory))
            dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ShowOpenTextFileDialog(string title, string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog();
        dialog.Title = title;
        dialog.Filter = "Shared parameters (*.txt)|*.txt|All files (*.*)|*.*";
        dialog.CheckFileExists = true;
        dialog.CheckPathExists = true;
        if (!string.IsNullOrWhiteSpace(initialDirectory))
            dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool? ShowSharedParameterPicker(object viewModel) => _presenter.ShowDialog(viewModel);

    public string? ShowSaveJsonDialog(string title, string? defaultFileName = null)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog();
        dialog.Title = title;
        dialog.Filter = "JSON files (*.json)|*.json";
        dialog.DefaultExt = ".json";
        dialog.AddExtension = true;
        dialog.OverwritePrompt = true;
        if (!string.IsNullOrWhiteSpace(defaultFileName))
            dialog.FileName = defaultFileName;
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool? ShowProperties(object viewModel) => _presenter.ShowDialog(viewModel);

    public string? ShowAssetOpenFileDialog(string title, FamilyAssetType assetType, string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog();
        dialog.Title = title;
        dialog.Filter = GetAssetFilter(assetType);
        dialog.CheckFileExists = true;
        if (initialDirectory is not null) dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool? ShowPresetEditor(object viewModel) => _presenter.ShowDialog(viewModel);

    public bool? ShowAttributeLibrary(object viewModel) => _presenter.ShowDialog(viewModel);

    public bool? ShowProfile(object viewModel) => _presenter.ShowDialog(viewModel);

    public bool? ShowBatchImportDialog(object viewModel) => _presenter.ShowDialog(viewModel);

    public bool? ShowValidationReport(object viewModel) => _presenter.ShowDialog(viewModel);

    public bool? ShowStatusDetails(object viewModel) => _presenter.ShowDialog(viewModel);

    public bool? ShowValidationRulesEditor(object viewModel) => _presenter.ShowDialog(viewModel);

    public bool? ShowAssignmentRulesEditor(object viewModel) => _presenter.ShowDialog(viewModel);

    /// <inheritdoc/>
    public void ShowModelessBatchImportDialog(object viewModel) => _presenter.ShowModeless(viewModel);

    /// <inheritdoc/>
    public void ShowDatabaseUpdateProgressDialog(object viewModel) => _presenter.ShowModeless(viewModel);

    /// <inheritdoc/>
    public bool? ShowAvatarCropper(object viewModel) => _presenter.ShowDialog(viewModel);

    /// <inheritdoc/>
    public bool? ShowRoutingPartPicker(object viewModel) => _presenter.ShowDialog(viewModel);

    public SharedFamiliesLoadChoice ShowSharedFamiliesLoadModeDialog(SharedFamilyDecisionRequest request)
    {
        var vm = ViewModels.SharedFamiliesLoadModeDialogViewModel.Create(request);
        var result = _presenter.ShowDialog(vm);
        if (result != true)
        {
            return SharedFamiliesLoadChoice.Skip;
        }
        return vm.Result;
    }

    private static string GetAssetFilter(FamilyAssetType assetType) => assetType switch
    {
        FamilyAssetType.Image => "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*",
        FamilyAssetType.Video => "Video files (*.mp4;*.avi;*.mov;*.wmv;*.mkv)|*.mp4;*.avi;*.mov;*.wmv;*.mkv|All files (*.*)|*.*",
        FamilyAssetType.Document => "Document files (*.pdf;*.doc;*.docx;*.txt;*.rtf)|*.pdf;*.doc;*.docx;*.txt;*.rtf|All files (*.*)|*.*",
        FamilyAssetType.Model3D => "3D Model files (*.glb;*.gltf;*.fbx;*.obj;*.stl)|*.glb;*.gltf;*.fbx;*.obj;*.stl|All files (*.*)|*.*",
        FamilyAssetType.LookupTable => "Lookup table files (*.csv;*.txt)|*.csv;*.txt|All files (*.*)|*.*",
        FamilyAssetType.Spreadsheet => "Spreadsheet files (*.xls;*.xlsx;*.xlsm)|*.xls;*.xlsx;*.xlsm|All files (*.*)|*.*",
        _ => "All files (*.*)|*.*"
    };
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// ViewModel for the batch import dialog.
/// </summary>
public sealed partial class FamilyBatchImportViewModel : ObservableObject, IObservableRequestClose
{
    public event Action<bool?>? RequestClose;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private bool _canImport;

    [ObservableProperty]
    private ObservableCollection<FamilyBatchImportRow> _items = new();

    private readonly IFamilyManagerDialogService _dialogService;
    private readonly IFamilyManagerViewModelFactory _viewModelFactory;

    public FamilyBatchImportViewModel(
        IReadOnlyList<FamilyBatchImportItem> items,
        IFamilyManagerDialogService dialogService,
        IFamilyManagerViewModelFactory viewModelFactory,
        string? defaultCategoryId = null)
    {
        _dialogService = dialogService;
        _viewModelFactory = viewModelFactory;

        foreach (var item in items)
        {
            var row = new FamilyBatchImportRow(item);
            row.PropertyChanged += OnRowPropertyChanged;
            row.PickCategoryRequested += OnRowPickCategoryRequested;
            Items.Add(row);
        }
        UpdateCanImport();
    }

    private async void OnRowPickCategoryRequested(FamilyBatchImportRow row)
    {
        var pickerVm = _viewModelFactory.CreateCategoryPickerViewModel();
        await pickerVm.InitializeAsync();
        var result = _dialogService.ShowCategoryPicker(pickerVm);
        if (result is not null)
        {
            if (string.IsNullOrEmpty(result))
            {
                row.TargetCategoryId = null;
                row.TargetCategoryPath = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "Без категории";
            }
            else
            {
                row.TargetCategoryId = result;
                row.TargetCategoryPath = pickerVm.SelectedPath;
            }
        }
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FamilyBatchImportRow.CanImport))
        {
            UpdateCanImport();
        }
    }

    private void UpdateCanImport()
    {
        CanImport = Items.Any(r => r.CanImport);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private void Import()
    {
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }

    /// <summary>
    /// Returns items with user-selected actions for the caller.
    /// </summary>
    public IReadOnlyList<FamilyBatchImportItem> GetResultItems()
    {
        return Items.Select(r => new FamilyBatchImportItem(
            r.FilePath,
            r.FileName,
            r.Sha256,
            r.RevitMajorVersion,
            r.FileSizeBytes,
            r.Status,
            r.ExistingCatalogItemId,
            r.ExistingVersionLabel,
            r.TargetCategoryId)
        {
            Action = r.Action
        }).ToList();
    }
}

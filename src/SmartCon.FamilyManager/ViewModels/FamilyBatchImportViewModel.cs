using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// ViewModel for the batch import dialog.
/// </summary>
public sealed partial class FamilyBatchImportViewModel : ObservableObject, IObservableRequestClose, IDisposable
{
    public event Action<bool?>? RequestClose;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private bool _canImport;

    [ObservableProperty]
    private ObservableCollection<FamilyBatchImportRow> _items = new();

    private readonly IFamilyManagerDialogService _dialogService;
    private readonly IFamilyManagerViewModelFactory _viewModelFactory;
    private readonly IFamilyCatalogProvider _catalogProvider;
    private int _statusLookupSeq;
    private bool _disposed;

    public FamilyBatchImportViewModel(
        IReadOnlyList<FamilyBatchImportItem> items,
        IFamilyManagerDialogService dialogService,
        IFamilyManagerViewModelFactory viewModelFactory,
        IFamilyCatalogProvider catalogProvider,
        string? defaultCategoryId = null,
        string? defaultCategoryName = null)
    {
        _dialogService = dialogService;
        _viewModelFactory = viewModelFactory;
        _catalogProvider = catalogProvider;

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.TargetCategoryId) && !string.IsNullOrEmpty(defaultCategoryId))
            {
                item.TargetCategoryId = defaultCategoryId;
                item.TargetCategoryName ??= defaultCategoryName;
            }
            var row = new FamilyBatchImportRow(item);
            row.PropertyChanged += OnRowPropertyChanged;
            row.PickCategoryRequested += OnRowPickCategoryRequestedAsync;
            row.NameChanged += OnRowNameChangedAsync;
            Items.Add(row);
        }
        UpdateCanImport();
    }

    private async Task OnRowPickCategoryRequestedAsync(FamilyBatchImportRow row)
    {
        try
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
        catch (Exception ex)
        {
            SmartConLogger.Error($"BatchImport.CategoryPicker: failed: {ex.Message}");
        }
    }

    private async Task OnRowNameChangedAsync(FamilyBatchImportRow row)
    {
        var seq = System.Threading.Interlocked.Increment(ref _statusLookupSeq);
        try
        {
            await UpdateStatusForRowAsync(row, seq).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (seq == Volatile.Read(ref _statusLookupSeq))
            {
                using var _scope = SmartConLogger.BeginScope("BatchImport", ("Method", "UpdateStatusForRow"));
                SmartConLogger.Warn($"failed: {ex.Message}");
            }
        }
    }

    private async Task UpdateStatusForRowAsync(FamilyBatchImportRow row, int seq)
    {
        if (!string.IsNullOrEmpty(row.Sha256))
        {
            var existingByHash = await _catalogProvider.FindByHashAsync(row.Sha256, CancellationToken.None).ConfigureAwait(true);
            if (seq != Volatile.Read(ref _statusLookupSeq)) return;
            if (existingByHash is not null)
            {
                row.SetStatusSilent(FamilyBatchImportStatus.Duplicate, existingByHash.CatalogItemId, existingByHash.VersionLabel);
                return;
            }
        }

        var normalizedName = FamilyNameNormalizer.Normalize(row.FileName);
        var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, CancellationToken.None).ConfigureAwait(true);
        if (seq != Volatile.Read(ref _statusLookupSeq)) return;

        if (existingByName is not null)
        {
            row.SetStatusSilent(FamilyBatchImportStatus.Existing, existingByName.Id, existingByName.CurrentVersionLabel);
        }
        else
        {
            row.SetStatusSilent(FamilyBatchImportStatus.New, null, null);
        }
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FamilyBatchImportRow.CanImport))
        {
            UpdateCanImport();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var row in Items)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
            row.PickCategoryRequested -= OnRowPickCategoryRequestedAsync;
            row.NameChanged -= OnRowNameChangedAsync;
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
            r.TargetCategoryId,
            r.TargetCategoryPath,
            r.FamilySource,
            r.TypeCount,
            r.RevitCategory)
        {
            Action = r.Action
        }).ToList();
    }
}

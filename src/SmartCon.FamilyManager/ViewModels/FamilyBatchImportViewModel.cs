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

    /// <summary>
    /// VM-owned set of currently selected rows. Maintained through
    /// <see cref="OnRowSelectionChanged"/> wired to each row's
    /// <c>IsSelected</c> property. This is the source of truth for batch
    /// operations, NOT <c>DataGrid.SelectedItems</c> — that one collapses
    /// when the user clicks an inline editor (ComboBox / Button) and would
    /// defeat the multi-select batch-apply UX.
    /// </summary>
    private readonly HashSet<FamilyBatchImportRow> _selectedRows = new();

    private readonly IFamilyManagerDialogService _dialogService;
    private readonly IFamilyManagerViewModelFactory _viewModelFactory;
    private readonly IFamilyCatalogProvider _catalogProvider;
    private int _statusLookupSeq;
    private bool _disposed;
    private bool _batchApplying;

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
            row.ActionChanged += OnRowActionChanged;
            row.CategoryChanged += OnRowCategoryChanged;
            row.SelectionChanged += OnRowSelectionChanged;
            Items.Add(row);
        }
        UpdateCanImport();
    }

    private void OnRowSelectionChanged(FamilyBatchImportRow row, bool isSelected)
    {
        if (isSelected)
        {
            _selectedRows.Add(row);
        }
        else
        {
            _selectedRows.Remove(row);
        }
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
                // OnTargetCategoryPathChanged partial-method on Row fires
                // ApplyCategoryToSelection, so the multi-select batch effect
                // is delivered without an explicit call here.
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"BatchImport.CategoryPicker: failed: {ex.Message}");
        }
    }

    private void OnRowActionChanged(FamilyBatchImportRow row, FamilyBatchImportAction newValue)
    {
        // Re-entrancy guard: when ApplyActionToSelection sets
        // target.Action = newValue below, that fires OnActionChanged on
        // the target, which would re-enter this method. The flag is
        // also checked inside ApplyActionToSelection itself for the
        // same reason.
        if (_batchApplying) return;
        ApplyActionToSelection(row, newValue);
    }

    private void OnRowCategoryChanged(FamilyBatchImportRow row, (string? Id, string Path) payload)
    {
        if (_batchApplying) return;
        ApplyCategoryToSelection(row, payload.Id, payload.Path);
    }

    private void ApplyActionToSelection(FamilyBatchImportRow source, FamilyBatchImportAction newValue)
    {
        if (_batchApplying) return;
        _batchApplying = true;
        try
        {
            foreach (var target in GetOtherSelectedRows(source))
            {
                if (target.AvailableActions.Contains(newValue))
                {
                    target.Action = newValue;
                }
                else
                {
                    SmartConLogger.Debug(
                        $"BatchImport.Action: skip apply {newValue} to '{target.FileName}' — not in AvailableActions");
                }
            }
        }
        finally
        {
            _batchApplying = false;
        }
    }

    private void ApplyCategoryToSelection(FamilyBatchImportRow source, string? id, string path)
    {
        if (_batchApplying) return;
        _batchApplying = true;
        try
        {
            foreach (var target in GetOtherSelectedRows(source))
            {
                target.TargetCategoryId = id;
                target.TargetCategoryPath = path;
            }
        }
        finally
        {
            _batchApplying = false;
        }
    }

    private List<FamilyBatchImportRow> GetOtherSelectedRows(FamilyBatchImportRow source)
    {
        // Exclude the source so the setter isn't fired twice (it would
        // still be idempotent but would emit an extra PropertyChanged and
        // a redundant UpdateCanImport cycle). The Count <= 1 fast-path
        // also covers the single-row selection case — when the user
        // changes Action on a single selected row there is nothing to
        // batch-apply.
        if (_selectedRows.Count <= 1) return new List<FamilyBatchImportRow>();
        return _selectedRows.Where(r => !ReferenceEquals(r, source)).ToList();
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
            row.ActionChanged -= OnRowActionChanged;
            row.CategoryChanged -= OnRowCategoryChanged;
            row.SelectionChanged -= OnRowSelectionChanged;
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

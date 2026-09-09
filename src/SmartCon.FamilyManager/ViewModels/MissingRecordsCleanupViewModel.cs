using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// ViewModel of the "Очистить недоступные записи" database tool (Issue #133).
/// The command is ALWAYS available with the database tools: opening it runs
/// the full scanner — the cheap -2 SQL pass PLUS an on-disk File.Exists scan
/// over every managed file (the primary detector: a file deleted manually is
/// invisible to the -2 marker until the next hash migration). The scan is
/// cancellable and potentially slow on unreachable network drives — the
/// permanent warning and the explicit confirmation guard against false
/// "missing" verdicts. Deletion reuses
/// <see cref="ICatalogActualizationService.PurgeMissingAsync"/> (best-effort:
/// rows are always deleted, unreachable directories are reported for manual
/// removal; dependency links to purged fittings are reset with a stale-routing
/// warning). A purged VERSION never kills the family while other versions
/// survive — the active pointer is switched automatically and reported.
/// </summary>
public sealed partial class MissingRecordsCleanupViewModel
    : ObservableObject, IObservableRequestClose, ICloseAwareViewModel, IDisposable
{
    private readonly ICatalogActualizationService _actualization;
    private readonly IFamilyManagerDialogService _dialogService;
    private readonly HashSet<string> _knownKeys = new(StringComparer.Ordinal);
    private CancellationTokenSource? _scanCts;

    private readonly TaskCompletionSource<bool?> _completionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<bool?>? RequestClose;

    public ObservableCollection<MissingRecordRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Scan progress (actualization-dialog pattern: "X of Y — file").</summary>
    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMaximum = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelectAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteAllCommand))]
    private bool _hasRows;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckDiskCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteAllCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    private bool _isScanRunning;

    [ObservableProperty]
    private string _deleteSelectedButtonText = string.Empty;

    /// <summary>True after at least one completed on-disk scan in this session.</summary>
    [ObservableProperty]
    private bool _isScanDone;

    /// <summary>
    /// True when this dialog deleted at least one row — the caller rebuilds
    /// the tree and refreshes the database-update state afterwards.
    /// </summary>
    public bool PurgedAny { get; private set; }

    public Task<bool?> DialogCompletion => _completionTcs.Task;

    public MissingRecordsCleanupViewModel(
        ICatalogActualizationService actualization,
        IFamilyManagerDialogService dialogService)
    {
        _actualization = actualization ?? throw new ArgumentNullException(nameof(actualization));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    /// <summary>
    /// Full initial scan (Issue #133), must be called AFTER the dialog is
    /// shown (modeless, ADR-048 pattern): runs the on-disk scanner over
    /// every managed file of the live database — a file deleted manually
    /// (even while Revit is running) is detected immediately, no migration
    /// and no restart needed. Rows fill incrementally; an interrupted scan
    /// keeps its partial results.
    /// </summary>
    public async Task RunScanAsync()
    {
        using var _scope = SmartConLogger.BeginScope("DbActualize",
            ("Method", nameof(RunScanAsync)));

        IsBusy = true;
        StatusText = LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_Searching)
            ?? "Поиск недоступных записей...";
        await RunDiskScanAsync().ConfigureAwait(true);
        IsBusy = false;
        UpdateFinalStatus();
    }

    [RelayCommand(CanExecute = nameof(CanCheckDisk))]
    private async Task CheckDiskAsync()
    {
        using var _scope = SmartConLogger.BeginScope("DbActualize",
            ("Method", nameof(CheckDiskAsync)));

        IsBusy = true;
        await RunDiskScanAsync().ConfigureAwait(true);
        IsBusy = false;
        UpdateFinalStatus();
    }

    private bool CanCheckDisk() => !IsBusy;

    /// <summary>
    /// The on-disk scan: drives the progress bar ("X of Y — file.rfa"),
    /// appends every discovered candidate to the list immediately.
    /// Returns false when interrupted.
    /// </summary>
    private async Task<bool> RunDiskScanAsync()
    {
        IsScanRunning = true;
        _scanCts = new CancellationTokenSource();
        var progress = new Progress<MissingRecordScanProgress>(OnScanProgress);
        try
        {
            await _actualization
                .ScanForMissingFilesAsync(progress, _scanCts.Token)
                .ConfigureAwait(true);
            IsScanDone = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = Rows.Count > 0
                ? LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_ScanCancelled)
                    ?? "Проверка прервана — показаны записи, найденные до остановки."
                : LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_ScanCancelledEmpty)
                    ?? "Проверка прервана — недоступные записи не найдены.";
            return false;
        }
        catch (Exception ex)
        {
            HandleScanFailure(ex, nameof(RunDiskScanAsync));
            return false;
        }
        finally
        {
            IsScanRunning = false;
        }
    }

    private void OnScanProgress(MissingRecordScanProgress p)
    {
        ProgressValue = p.Current;
        ProgressMaximum = Math.Max(1, p.Total);
        StatusText = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_ScanProgress)
                ?? "Проверка {0} из {1} — {2}",
            p.Current, p.Total, p.CurrentFileName);
        if (p.Found is not null)
        {
            AddRow(p.Found);
        }
    }

    [RelayCommand(CanExecute = nameof(IsScanRunning))]
    private void CancelScan()
    {
        if (_scanCts is null || _scanCts.IsCancellationRequested) return;
        _scanCts.Cancel();
        StatusText = LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_Stopping)
            ?? "Прерываю...";
    }

    private void HandleScanFailure(Exception ex, string method)
    {
        SmartConLogger.Error(
            $"Missing-record scan failed ({method}): {ex.GetType().Name}: {ex.Message} " +
            $"[Action: проверьте доступность диска с базой и повторите «Проверить диск»]");
        _dialogService.ShowError(
            LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_Title)
                ?? "Очистка недоступных записей",
            ex.Message);
    }

    /// <summary>
    /// Single final status over the merged list: total found or "nothing
    /// found" — the user should not have to sum up "-2 marked" and "scan
    /// found more" lines.
    /// </summary>
    private void UpdateFinalStatus()
    {
        StatusText = Rows.Count == 0
            ? LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_NothingFound)
                ?? "Недоступные записи не найдены."
            : string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_FoundCount)
                    ?? "Найдено недоступных записей: {0}.",
                Rows.Count);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private Task DeleteSelectedAsync() => PurgeRowsAsync(Rows.Where(r => r.IsSelected).ToList());

    private bool CanDeleteSelected() => !IsBusy && Rows.Any(r => r.IsSelected);

    [RelayCommand(CanExecute = nameof(CanDeleteAll))]
    private Task DeleteAllAsync() => PurgeRowsAsync(Rows.ToList());

    private bool CanDeleteAll() => !IsBusy && Rows.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSelectAll))]
    private void SelectAll()
    {
        foreach (var row in Rows) row.IsSelected = true;
    }

    [RelayCommand(CanExecute = nameof(CanSelectAll))]
    private void ClearSelection()
    {
        foreach (var row in Rows) row.IsSelected = false;
    }

    private bool CanSelectAll() => !IsBusy && Rows.Count > 0;

    private async Task PurgeRowsAsync(IReadOnlyList<MissingRecordRowViewModel> rows)
    {
        if (rows.Count == 0) return;

        var confirmed = _dialogService.ShowConfirmation(
            LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_ConfirmTitle)
                ?? "Удаление записей",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_ConfirmBody)
                    ?? "Удалить из каталога {0} записей о недоступных файлах? Файлы могут быть временно недоступны (например, отключён сетевой диск) — удаление необратимо.",
                rows.Count));
        if (!confirmed) return;

        using var _scope = SmartConLogger.BeginScope("DbActualize",
            ("Method", nameof(PurgeRowsAsync)),
            ("Count", rows.Count));

        IsBusy = true;
        try
        {
            var missing = rows
                .Select(r => new HashRecalculationMissingFile(
                    r.CatalogItemId, r.ItemName, r.VersionLabel, r.FileName))
                .ToList();
            var result = await _actualization
                .PurgeMissingAsync(missing, CancellationToken.None)
                .ConfigureAwait(true);

            PurgedAny |= result.DeletedItems > 0 || result.DeletedVersions > 0;

            var resultText = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_PurgeResult)
                    ?? "Удалено семейств: {0}, версий: {1}.",
                result.DeletedItems, result.DeletedVersions);
            if (result.SwitchedActiveVersions.Count > 0)
            {
                resultText += Environment.NewLine + (
                    LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_PurgeSwitchedActive)
                        ?? "Активная версия переключена на оставшуюся:");
                resultText += Environment.NewLine + DatabaseUpdateProgressViewModel
                    .FormatSwitchedActiveList(result.SwitchedActiveVersions);
            }
            if (result.ResetRoutingLinks.Count > 0)
            {
                resultText += Environment.NewLine + (
                    LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_PurgeResetLinks)
                        ?? "Сброшены элементы трассировки (фитинг удалён из каталога):");
                resultText += Environment.NewLine + DatabaseUpdateProgressViewModel
                    .FormatResetRoutingList(result.ResetRoutingLinks);
                resultText += Environment.NewLine + (
                    LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_PurgeResetLinksNote)
                        ?? "Если удалённый фитинг использовался в трассировке в проекте, эта трассировка стала устаревшей: замените фитинг в окне свойств семейства или удалите его из проекта.");
            }
            if (result.FailedDirectories > 0)
            {
                resultText += Environment.NewLine + string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_PurgeDirsFailed)
                        ?? "Папки на диске удалить не удалось (нет доступа): {0} — удалите их вручную.",
                    result.FailedDirectories);
            }

            // Re-read the surviving -2 candidates so rows behind a failed
            // delete stay visible instead of being optimistically removed.
            var survivors = await _actualization
                .LoadMissingRecordCandidatesAsync()
                .ConfigureAwait(true);
            ApplyRows(survivors);
            // File-missing rows found by the scan are not re-loaded — the
            // user re-runs "Проверить диск" if needed.
            StatusText = resultText;
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"Missing-record purge failed: {ex.GetType().Name}: {ex.Message} " +
                $"[Action: проверьте лог smartcon.log; уже удалённые записи не восстановятся]");
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_Title)
                    ?? "Очистка недоступных записей",
                ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close()
    {
        _completionTcs.TrySetResult(true);
        RequestClose?.Invoke(true);
    }

    public void ConfirmClose(CloseConfirmationArgs args)
    {
        if (IsBusy)
        {
            // X while busy keeps the window open (the actualization-dialog
            // pattern): during a scan it also requests cancellation — rows
            // found before the stop stay visible; during a short purge it
            // simply waits for the operation to finish.
            args.Cancel = true;
            if (IsScanRunning) CancelScan();
            return;
        }

        args.DialogResult = true;
        _completionTcs.TrySetResult(true);
    }

    public void Dispose()
    {
        // Best-effort cancel only, NO Dispose: a long SMB scan may still be
        // reading the token inside Task.Run — disposing it here would throw
        // ObjectDisposedException into the generic catch (and an error dialog
        // after the window is already closed).
        _scanCts?.Cancel();
        _completionTcs.TrySetResult(false);
    }

    private void ApplyRows(IEnumerable<MissingRecordCandidate> candidates)
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }
        Rows.Clear();
        _knownKeys.Clear();
        foreach (var candidate in candidates)
        {
            AddRow(candidate);
        }
        HasRows = Rows.Count > 0;
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        UpdateSelectedCountText();
    }

    /// <summary>Adds a row unless the (item, label) pair is already listed.</summary>
    private bool AddRow(MissingRecordCandidate candidate)
    {
        if (!_knownKeys.Add(candidate.CatalogItemId + "|" + candidate.VersionLabel)) return false;
        var row = new MissingRecordRowViewModel(candidate);
        row.PropertyChanged += OnRowPropertyChanged;
        Rows.Add(row);
        if (!HasRows) HasRows = true;
        DeleteAllCommand.NotifyCanExecuteChanged();
        return true;
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MissingRecordRowViewModel.IsSelected)) return;
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        UpdateSelectedCountText();
    }

    private void UpdateSelectedCountText()
    {
        DeleteSelectedButtonText = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_DeleteSelected)
                ?? "Удалить выбранные ({0})",
            Rows.Count(r => r.IsSelected));
    }
}

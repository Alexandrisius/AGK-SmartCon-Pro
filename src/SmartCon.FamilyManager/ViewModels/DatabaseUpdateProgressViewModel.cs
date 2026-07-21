using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// ViewModel for the UNIFIED database update dialog (ADR-054): ONE modeless
/// window (ADR-048 pattern) that runs the actualization engine
/// (<see cref="ICatalogActualizationService.RunAllPendingAsync"/>) over
/// every pending family — one file open, all pending tasks applied — then
/// shows a combined summary with an optional purge action for missing
/// files.
/// </summary>
public sealed partial class DatabaseUpdateProgressViewModel
    : ObservableObject, IObservableRequestClose, ICloseAwareViewModel, IDisposable
{
    private readonly ICatalogActualizationService _actualization;
    private readonly IFamilyManagerDialogService _dialogService;
    private readonly int _currentRevitVersion;
    private CancellationTokenSource? _runCts;
    private DatabaseMigrationResult? _result;
    private bool _isRunning;

    private readonly TaskCompletionSource<bool?> _completionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<bool?>? RequestClose;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMaximum = 1;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private bool _isSummaryVisible;

    /// <summary>Visible only on the summary screen when missing files exist.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PurgeMissingCommand))]
    private bool _isPurgeVisible;

    [ObservableProperty]
    private string _purgeButtonText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _canCancel = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private bool _canClose;

    public Task<bool?> DialogCompletion => _completionTcs.Task;

    public DatabaseUpdateProgressViewModel(
        ICatalogActualizationService actualization,
        IFamilyManagerDialogService dialogService,
        int currentRevitVersion)
    {
        _actualization = actualization ?? throw new ArgumentNullException(nameof(actualization));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _currentRevitVersion = currentRevitVersion;
        StatusText = LanguageManager.GetString(StringLocalization.Keys.FM_DbUpdate_Starting)
            ?? "Подготовка...";
    }

    /// <summary>
    /// Runs all pending migrations. Must be called AFTER the dialog is
    /// shown. Completes when the summary screen is displayed (the dialog
    /// stays open until the user closes it — await
    /// <see cref="DialogCompletion"/> for that).
    /// </summary>
    public async Task RunAsync()
    {
        using var _scope = SmartConLogger.BeginScope("DbMigration",
            ("Method", nameof(RunAsync)),
            ("RevitVersion", _currentRevitVersion));

        _isRunning = true;
        CanClose = false;
        CanCancel = true;
        _runCts = new CancellationTokenSource();
        var progress = new Progress<DatabaseMigrationProgress>(OnProgress);

        try
        {
            _result = await Task.Run(
                () => _actualization.RunAllPendingAsync(
                    _currentRevitVersion, progress, _runCts.Token));
        }
        catch (OperationCanceledException)
        {
            // Defensive: the engine handles cancel between files and
            // returns WasCancelled=true; an OCE escaping anyway must still
            // render the cancelled summary, not a fake failure.
            _result = DatabaseMigrationResult.Cancelled;
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"Database update failed: {ex.GetType().Name}: {ex.Message} " +
                $"[Action: проверьте лог smartcon.log; БД остаётся консистентной — обновлённые пачки закоммичены]");
            _result = new DatabaseMigrationResult(
                0, 0,
                Array.Empty<HashRecalculationMissingFile>(),
                new[] { new HashRecalculationFailedFile("—", "—", "—", ex.Message) },
                WasCancelled: false);
        }

        _isRunning = false;
        ShowSummary();
    }

    private void OnProgress(DatabaseMigrationProgress p)
    {
        ProgressValue = p.Current;
        ProgressMaximum = Math.Max(1, p.Total);
        StatusText = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_DbUpdate_ProgressFormat)
                ?? "Обработка {0} из {1} — {2}",
            p.Current, p.Total, p.CurrentFileName);
    }

    private void ShowSummary()
    {
        var result = _result!;
        var lines = new List<string>
        {
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbUpdate_SummaryUpdated)
                    ?? "Обновлено записей: {0}",
                result.UpdatedCount)
        };

        if (result.FailedFiles.Count > 0)
        {
            lines.Add(string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbUpdate_SummaryFailed)
                    ?? "Не удалось обработать (будут предложены снова): {0}",
                result.FailedFiles.Count));
        }

        if (result.NewerRevitCount > 0)
        {
            lines.Add(string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbUpdate_SummaryNewerRevit)
                    ?? "Требуют более новой версии Revit: {0}",
                result.NewerRevitCount));
        }

        if (result.MissingFiles.Count > 0)
        {
            lines.Add(string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbUpdate_SummaryMissing)
                    ?? "Файлы не найдены на диске: {0}",
                result.MissingFiles.Count));
            PurgeButtonText = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_PurgeButton)
                    ?? "Удалить записи недоступных ({0})",
                result.MissingFiles.Count);
            IsPurgeVisible = true;
        }

        if (result.WasCancelled)
        {
            lines.Add(LanguageManager.GetString(StringLocalization.Keys.FM_DbUpdate_SummaryCancelled)
                ?? "Прервано пользователем — обновление продолжится при следующем запуске.");
        }

        SummaryText = string.Join(Environment.NewLine, lines);
        StatusText = string.Empty;
        // CanClose BEFORE IsSummaryVisible: observers reacting to the
        // summary flag (auto-close in tests, UX rules) must already see
        // the Close command enabled — otherwise DialogCompletion never
        // completes and the caller deadlocks.
        CanCancel = false;
        CanClose = true;
        IsSummaryVisible = true;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_runCts is null || _runCts.IsCancellationRequested) return;
        _runCts.Cancel();
        CanCancel = false;
        StatusText = LanguageManager.GetString(StringLocalization.Keys.FM_DbUpdate_Stopping)
            ?? "Прерываю...";
    }

    [RelayCommand(CanExecute = nameof(IsPurgeVisible))]
    private async Task PurgeMissingAsync()
    {
        var missing = _result?.MissingFiles;
        if (missing is null || missing.Count == 0) return;

        var confirmed = _dialogService.ShowConfirmation(
            LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_PurgeConfirmTitle)
                ?? "Удаление записей",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_PurgeConfirmBody)
                    ?? "Удалить из каталога {0} записей о недоступных файлах? Действие нельзя отменить.",
                missing.Count));
        if (!confirmed) return;

        using var _scope = SmartConLogger.BeginScope("DbMigration",
            ("Method", nameof(PurgeMissingAsync)),
            ("Count", missing.Count));

        try
        {
            var (deletedItems, deletedVersions, failedDirectories) = await _actualization
                .PurgeMissingAsync(missing, CancellationToken.None)
                .ConfigureAwait(true);

            IsPurgeVisible = false;
            SummaryText += Environment.NewLine + string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_PurgeResult)
                    ?? "Удалено семейств: {0}, версий: {1}.",
                deletedItems, deletedVersions);
            if (failedDirectories > 0)
            {
                SummaryText += Environment.NewLine + string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_PurgeDirsFailed)
                        ?? "Папки на диске удалить не удалось (нет доступа): {0} — удалите их вручную.",
                    failedDirectories);
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"PurgeMissing failed: {ex.GetType().Name}: {ex.Message}");
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_PurgeConfirmTitle)
                    ?? "Удаление записей",
                ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close()
    {
        _completionTcs.TrySetResult(true);
        RequestClose?.Invoke(true);
    }

    public void ConfirmClose(CloseConfirmationArgs args)
    {
        if (_isRunning)
        {
            // X during the run = request cancellation; the dialog closes
            // from the summary screen.
            args.Cancel = true;
            Cancel();
            return;
        }

        args.DialogResult = true;
        _completionTcs.TrySetResult(true);
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
        _completionTcs.TrySetResult(false);
    }
}

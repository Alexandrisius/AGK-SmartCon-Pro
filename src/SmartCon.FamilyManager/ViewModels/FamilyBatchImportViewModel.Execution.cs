using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Threading;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyBatchImportViewModel
{
    private enum BatchDialogState
    {
        Setup,
        Importing,
        Stopping,
        Paused,
        Summary
    }

    private BatchDialogState _state = BatchDialogState.Setup;
    private readonly PauseGate _pauseGate = new();
    private CancellationTokenSource? _importCts;
    private readonly TaskCompletionSource<bool?> _completionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isImporting;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isStopping;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isPaused;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isSummary;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isGridEnabled = true;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isSecondaryVisible = true;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private double _progressValue;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private double _progressMaximum = 1;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _statusText = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _summaryMessage = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _primaryButtonText = string.Empty;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _secondaryButtonText = string.Empty;

    public Task<bool?> DialogCompletion => _completionTcs.Task;

    public bool ImportStarted { get; private set; }

    public int ImportSuccessCount { get; private set; }

    public int ImportSkippedCount { get; private set; }

    public int ImportErrorCount { get; private set; }

    private void InitializeExecutionState()
    {
        PrimaryButtonText = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Load) ?? "Загрузить";
        SecondaryButtonText = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Cancel) ?? "Отмена";
    }

    private bool CanExecutePrimary() => _state switch
    {
        BatchDialogState.Setup => CanImport,
        BatchDialogState.Paused => true,
        BatchDialogState.Summary => true,
        _ => false
    };

    private bool CanExecuteSecondary() =>
        _state is BatchDialogState.Setup or BatchDialogState.Importing or BatchDialogState.Paused;

    [RelayCommand(CanExecute = nameof(CanExecutePrimary), AllowConcurrentExecutions = true)]
    private async Task Import()
    {
        switch (_state)
        {
            case BatchDialogState.Setup:
                if (_executor is null)
                {
                    Close(true);
                    return;
                }
                await RunImportAsync();
                break;
            case BatchDialogState.Paused:
                _state = BatchDialogState.Importing;
                StatusText = string.Empty;
                _pauseGate.Resume();
                SyncStateProperties();
                break;
            case BatchDialogState.Summary:
                Close(true);
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSecondary))]
    private void Cancel()
    {
        SmartConLogger.Debug(
            $"BatchImport: Cancel pressed (state={_state}, pendingValidation={_pendingValidation is not null}, " +
            $"blockedRows={Items.Count(r => r.IsGateBlocked)})");
        switch (_state)
        {
            case BatchDialogState.Setup:
                Close(false);
                break;
            case BatchDialogState.Importing:
                RequestStop();
                break;
            case BatchDialogState.Paused:
                CloseAfterStop();
                break;
        }
    }

    private void RequestStop()
    {
        if (_state != BatchDialogState.Importing) return;
        _pauseGate.Pause();
        _state = BatchDialogState.Stopping;
        StatusText = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Stopping) ?? "Останавливаю...";
        SyncStateProperties();
    }

    private void CloseAfterStop()
    {
        _importCts?.Cancel();
        _pauseGate.Resume();
    }

    private async Task RunImportAsync()
    {
        ImportStarted = true;
        _importCts?.Dispose();
        _importCts = new CancellationTokenSource();
        _state = BatchDialogState.Importing;
        ProgressValue = 0;
        ProgressMaximum = Items.Count;
        SyncStateProperties();

        Task[] pendingRenames;
        lock (_pendingNameChangesLock)
        {
            pendingRenames = _pendingNameChanges.Values.Select(v => v.Task).ToArray();
        }
        if (pendingRenames.Length > 0)
        {
            SmartConLogger.Info(
                $"BatchImport: awaiting {pendingRenames.Length} pending name-change recomputation(s) before snapshotting rows");
            try
            {
                await Task.WhenAll(pendingRenames);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SmartConLogger.Warn(
                    $"BatchImport: name-change recomputation failed while awaiting before import: {ex.Message} " +
                    $"[Action: проверьте, что БД каталога доступна; импорт продолжится с текущими данными строк]");
            }
            catch (OperationCanceledException)
            {
            }
        }

        // Import Validation Gate: an in-flight revalidation (category was
        // changed just before pressing Import) must complete first —
        // otherwise the import snapshots a stale gate verdict.
        var pendingValidation = _pendingValidation;
        if (pendingValidation is not null)
        {
            try
            {
                await pendingValidation;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SmartConLogger.Warn(
                    $"BatchImport: gate revalidation failed while awaiting before import: {ex.Message} " +
                    $"[Action: импорт продолжится с текущими данными строк]");
            }
        }

        var items = GetResultItems();
        if (!string.IsNullOrEmpty(_publishedByUser))
        {
            foreach (var item in items)
            {
                item.PublishedByUser = _publishedByUser;
            }
        }
        var progress = new Progress<FamilyBatchImportProgress>(OnExecutionProgress);

        FamilyBatchImportExecutionResult result;
        try
        {
            result = await Task.Run(
                () => _executor!.ExecuteAsync(items, _categoryId, progress, _pauseGate, _importCts.Token));
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"BatchImport execution failed: {ex.GetType().Name}: {ex.Message}");
            result = new FamilyBatchImportExecutionResult(0, 0, items.Count, false);
        }

        ImportSuccessCount = result.SuccessCount;
        ImportSkippedCount = result.SkippedCount;
        ImportErrorCount = result.ErrorCount;

        if (result.WasStopped)
        {
            SmartConLogger.Info(
                $"BatchImport stopped by user: success={result.SuccessCount}, skipped={result.SkippedCount}, errors={result.ErrorCount}");
            Close(null);
            return;
        }

        SummaryMessage = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_SummaryFormat)
                ?? "Импортировано: {0}, пропущено: {1}, ошибок: {2}",
            result.SuccessCount, result.SkippedCount, result.ErrorCount);
        StatusText = string.Empty;
        _state = BatchDialogState.Summary;
        SyncStateProperties();
    }

    private void OnExecutionProgress(FamilyBatchImportProgress p)
    {
        if (p.Phase == FamilyBatchImportPhase.Paused)
        {
            _state = BatchDialogState.Paused;
            StatusText = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Paused)
                    ?? "Остановлено: обработано {0} из {1}",
                p.CurrentIndex, p.Total);
            SyncStateProperties();
            return;
        }

        if (p.CurrentIndex < 0 || p.CurrentIndex >= Items.Count)
        {
            if (p.Phase == FamilyBatchImportPhase.Finalizing)
            {
                StatusText = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Finalizing)
                    ?? "Завершение...";
            }
            return;
        }

        var row = Items[p.CurrentIndex];
        if (p.ItemState is null)
        {
            row.ImportRowState = FamilyBatchImportRowState.Running;
            ProgressValue = p.CurrentIndex;
            StatusText = string.Format(
                GetPhaseFormat(p.Phase), p.CurrentIndex + 1, p.Total, p.CurrentItemName);
        }
        else
        {
            row.ImportRowState = p.ItemState.Value;
            row.ImportErrorMessage = p.ItemError;
            ProgressValue = p.CurrentIndex + 1;
        }
    }

    private static string GetPhaseFormat(FamilyBatchImportPhase phase) => phase switch
    {
        FamilyBatchImportPhase.Staging =>
            LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_ProgressStaging)
                ?? "Подготовка {0} из {1} — {2}",
        FamilyBatchImportPhase.Extracting =>
            LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_ProgressExtracting)
                ?? "Обработка {0} из {1} — {2}",
        _ =>
            LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_ProgressImporting)
                ?? "Импорт {0} из {1} — {2}"
    };

    private void SyncStateProperties()
    {
        IsImporting = _state is BatchDialogState.Importing or BatchDialogState.Stopping or BatchDialogState.Paused;
        IsStopping = _state == BatchDialogState.Stopping;
        IsPaused = _state == BatchDialogState.Paused;
        IsSummary = _state == BatchDialogState.Summary;
        IsGridEnabled = _state == BatchDialogState.Setup;
        IsSecondaryVisible = _state != BatchDialogState.Summary;

        PrimaryButtonText = _state switch
        {
            BatchDialogState.Paused =>
                LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Resume) ?? "Продолжить",
            BatchDialogState.Summary =>
                LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_OK) ?? "OK",
            _ =>
                LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Load) ?? "Загрузить"
        };

        SecondaryButtonText = _state switch
        {
            BatchDialogState.Importing or BatchDialogState.Stopping =>
                LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Stop) ?? "Остановить",
            BatchDialogState.Paused =>
                LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Close) ?? "Закрыть",
            _ =>
                LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Cancel) ?? "Отмена"
        };

        ImportCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void Close(bool? result)
    {
        _isClosing = true;
        SmartConLogger.Debug($"BatchImport: Close(result={result})");
        _completionTcs.TrySetResult(result);
        RequestClose?.Invoke(result);
    }

    public void ConfirmClose(CloseConfirmationArgs args)
    {
        // _isClosing ставится только в ветках, реально ведущих к закрытию:
        // в Importing/Stopping диалог остаётся жить (args.Cancel = true),
        // и застрявший флаг молча сломал бы любую будущую ревалидацию.
        switch (_state)
        {
            case BatchDialogState.Importing:
                args.Cancel = true;
                RequestStop();
                break;
            case BatchDialogState.Stopping:
                args.Cancel = true;
                break;
            case BatchDialogState.Paused:
                args.Cancel = true;
                _isClosing = true;
                CloseAfterStop();
                break;
            case BatchDialogState.Summary:
                _isClosing = true;
                args.DialogResult = true;
                _completionTcs.TrySetResult(true);
                break;
            default:
                _isClosing = true;
                args.DialogResult = false;
                _completionTcs.TrySetResult(false);
                break;
        }
    }

    private void DisposeExecution()
    {
        _importCts?.Cancel();
        _importCts?.Dispose();
        _importCts = null;
        _pauseGate.Resume();
        _completionTcs.TrySetResult(false);
    }
}

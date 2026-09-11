using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels.Cloud;

/// <summary>
/// Modeless-прогресс облачной операции (ADR-048, образец —
/// <see cref="DatabaseUpdateProgressViewModel"/>): publish и pull едут в
/// одном диалоге, панель не блокируется, отмена через CTS, итог — сводка в
/// том же окне до закрытия пользователем.
/// </summary>
public sealed partial class CloudOperationProgressViewModel
    : ObservableObject, IObservableRequestClose, ICloseAwareViewModel, IDisposable
{
    private readonly Func<IProgress<CloudOperationProgress>, CancellationToken, Task<string>> _operation;
    private CancellationTokenSource? _runCts;
    private bool _isRunning;
    private string? _finalStatus;

    private readonly TaskCompletionSource<bool?> _completionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<bool?>? RequestClose;

    [ObservableProperty] private string _title;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMaximum = 1;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private bool _isSummaryVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _canCancel = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private bool _canClose;

    /// <summary>Итоговая строка статуса (локализована) — null при ошибке/отмене.</summary>
    public string? FinalStatus => _finalStatus;

    public bool Failed { get; private set; }

    public Task<bool?> DialogCompletion => _completionTcs.Task;

    public CloudOperationProgressViewModel(
        string title,
        Func<IProgress<CloudOperationProgress>, CancellationToken, Task<string>> operation)
    {
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));
        _title = title;
    }

    /// <summary>Вызывать ПОСЛЕ показа окна. Завершается при показе сводки.</summary>
    public async Task RunAsync()
    {
        _isRunning = true;
        CanClose = false;
        CanCancel = true;
        _runCts = new CancellationTokenSource();
        var progress = new Progress<CloudOperationProgress>(OnProgress);

        string summary;
        try
        {
            summary = await Task.Run(() => _operation(progress, _runCts.Token)).ConfigureAwait(true);
            _finalStatus = summary;
            Failed = false;
        }
        catch (OperationCanceledException)
        {
            summary = LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_Cancelled) ?? "Операция отменена";
            _finalStatus = null;
        }
        catch (Exception ex)
        {
            Failed = true;
            _finalStatus = null;
            SmartConLogger.Error(
                $"Cloud operation failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: подробности в smartcon.log; повторите операцию после устранения причины]");
            summary = ex.Message;
        }

        _isRunning = false;
        SummaryText = summary;
        StatusText = string.Empty;
        // CanClose ДО IsSummaryVisible (см. DatabaseUpdateProgressViewModel —
        // иначе наблюдатели сводки deadlock'ят DialogCompletion).
        CanCancel = false;
        CanClose = true;
        IsSummaryVisible = true;
    }

    private void OnProgress(CloudOperationProgress p)
    {
        var stage = p.Stage switch
        {
            "BuildManifest" => LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_StageBuildManifest) ?? "Подготовка манифеста",
            "Upload" => LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_StageUpload) ?? "Загрузка файлов на сервер",
            "Download" => LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_StageDownload) ?? "Загрузка объектов",
            "Apply" => LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_StageApply) ?? "Применение манифеста",
            _ => p.Stage,
        };
        ProgressValue = p.Current;
        ProgressMaximum = Math.Max(1, p.Total);
        StatusText = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_Cloud_ProgressFormat) ?? "{0}: {1} / {2}",
            stage, p.Current, p.Total);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_runCts is null || _runCts.IsCancellationRequested) return;
        _runCts.Cancel();
        CanCancel = false;
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
            // X во время работы = отмена; окно закроется со сводки.
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

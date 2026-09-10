namespace SmartCon.Core.Threading;

public sealed class PauseGate
{
    private volatile TaskCompletionSource<bool>? _pauseTcs;

    public bool IsPaused => _pauseTcs is not null;

    public void Pause()
    {
        Interlocked.CompareExchange(
            ref _pauseTcs,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            null);
    }

    public void Resume()
    {
        Interlocked.Exchange(ref _pauseTcs, null)?.TrySetResult(true);
    }

    public Task WaitWhilePausedAsync()
    {
        return _pauseTcs?.Task ?? Task.CompletedTask;
    }
}

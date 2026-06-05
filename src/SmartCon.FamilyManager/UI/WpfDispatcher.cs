using System.Windows.Threading;
using SmartCon.Core.Common;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.UI;

public sealed class WpfDispatcher : IDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfDispatcher() : this(System.Windows.Application.Current?.Dispatcher) { }

    public WpfDispatcher(Dispatcher? dispatcher) => _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;

    public bool CheckAccess() => _dispatcher?.CheckAccess() ?? true;

    public void Invoke(Action action)
    {
        Guard.ThrowIfNull(action);
        if (_dispatcher is null || _dispatcher.CheckAccess()) action();
        else _dispatcher.Invoke(action);
    }

    public Task InvokeAsync(Action action, CancellationToken ct = default)
    {
        Guard.ThrowIfNull(action);
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        var task = _dispatcher.InvokeAsync(action).Task;
        if (ct.CanBeCanceled && !task.IsCompleted)
        {
            return AwaitWithCancellation(task, ct);
        }
        return task;
    }

    private static async Task AwaitWithCancellation(Task task, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => tcs.TrySetCanceled(ct)))
        {
            var completed = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
            if (completed == tcs.Task) ct.ThrowIfCancellationRequested();
        }
        await task.ConfigureAwait(false);
    }
}

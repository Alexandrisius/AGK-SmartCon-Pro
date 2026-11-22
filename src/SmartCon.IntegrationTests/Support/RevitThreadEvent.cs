using System.Windows.Threading;
using Autodesk.Revit.ApplicationServices;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.IntegrationTests.Support;

/// <summary>
/// Test-host implementation of <see cref="IAgentAwaitableEvent"/>. The TUnit
/// Revit executor runs every test on the Revit API thread, but the production
/// orchestrator awaits with <c>ConfigureAwait(false)</c>, so a tool call reaches
/// this class on a thread-pool thread where Revit refuses writes ("Cannot
/// modify the document"). The call is therefore dispatched to the Revit thread's
/// dispatcher, which the benchmark pumps while the turn runs (see
/// AgentRealCasesBenchmarkTests.Production). On the Revit thread itself, or
/// when the thread has no dispatcher, it executes inline.
/// </summary>
internal sealed class RevitThreadEvent : IAgentAwaitableEvent
{
    private readonly Application _application;
    private readonly Dispatcher? _revitDispatcher;
    private readonly Thread _revitThread;

    public RevitThreadEvent(Application application)
    {
        _application = application;
        _revitThread = Thread.CurrentThread;
        _revitDispatcher = Dispatcher.FromThread(_revitThread);
    }

    public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
    {
        if (IsOnRevitThread())
            return Task.FromResult(funcWithApp(_application));

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatch(
            () =>
            {
                try
                {
                    completion.SetResult(funcWithApp(_application));
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
        return completion.Task;
    }

    public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
    {
        if (IsOnRevitThread())
        {
            actionWithApp(_application);
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatch(
            () =>
            {
                try
                {
                    actionWithApp(_application);
                    completion.SetResult(true);
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
        return completion.Task;
    }

    public void ProcessQueue(object revitApp)
    {
    }

    public void Initialize(Action onRaise)
    {
    }

    private bool IsOnRevitThread() =>
        _revitDispatcher is null || Thread.CurrentThread == _revitThread;

    private void Dispatch(Action action) =>
        _revitDispatcher!.BeginInvoke(DispatcherPriority.Normal, action);
}

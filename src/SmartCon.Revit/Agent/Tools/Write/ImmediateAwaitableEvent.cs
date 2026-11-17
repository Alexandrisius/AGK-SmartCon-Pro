using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Test seam for write tools: executes the queued action on the calling thread.
/// Integration tests run on the Revit API thread, so no marshalling is needed.
/// </summary>
internal sealed class ImmediateAwaitableEvent : IAgentAwaitableEvent
{
    public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(funcWithApp(this));
    }

    public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        actionWithApp(this);
        return Task.CompletedTask;
    }

    public void ProcessQueue(object revitApp)
    {
    }

    public void Initialize(Action onRaise)
    {
    }
}

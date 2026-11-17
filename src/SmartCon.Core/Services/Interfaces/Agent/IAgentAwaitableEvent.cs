namespace SmartCon.Core.Services.Interfaces.Agent;

public interface IAgentAwaitableEvent
{
    Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default);

    Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default);

    void ProcessQueue(object revitApp);

    void Initialize(Action onRaise);
}
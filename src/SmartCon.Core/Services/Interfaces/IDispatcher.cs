namespace SmartCon.Core.Services.Interfaces;

public interface IDispatcher
{
    bool CheckAccess();

    void Invoke(Action action);

    Task InvokeAsync(Action action, CancellationToken ct = default);
}

using Autodesk.Revit.ApplicationServices;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.IntegrationTests.Support;

/// <summary>
/// Test-host implementation of <see cref="IAgentAwaitableEvent"/>: the TUnit
/// Revit executor already runs every test on the single Revit API thread, so
/// the "queue" executes inline instead of through an ExternalEvent. Extracted
/// from AgentMultiModelTests for reuse by the production agent host.
/// </summary>
internal sealed class RevitThreadEvent : IAgentAwaitableEvent
{
    private readonly Application _application;

    public RevitThreadEvent(Application application)
    {
        _application = application;
    }

    public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default) =>
        Task.FromResult(funcWithApp(_application));

    public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
    {
        actionWithApp(_application);
        return Task.CompletedTask;
    }

    public void ProcessQueue(object revitApp)
    {
    }

    public void Initialize(Action onRaise)
    {
    }
}

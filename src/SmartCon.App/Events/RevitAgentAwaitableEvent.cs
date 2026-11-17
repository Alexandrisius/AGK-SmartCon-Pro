#if REVIT2023_OR_GREATER
using Autodesk.Revit.UI;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.App.Events;

internal sealed class RevitAgentAwaitableEvent : IExternalEventHandler
{
    private readonly IAgentAwaitableEvent _awaitable;

    public RevitAgentAwaitableEvent(IAgentAwaitableEvent awaitable)
    {
        _awaitable = awaitable ?? throw new ArgumentNullException(nameof(awaitable));
    }

    public void Execute(UIApplication app) => _awaitable.ProcessQueue(app);

    public string GetName() => "SmartCon.Agent.AwaitableEvent";
}
#endif
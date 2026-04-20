using System.Threading;
using Autodesk.Revit.UI;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.PipeConnect.Events;

public sealed class PipeConnectExternalEvent : IExternalEventHandler
{
    private readonly IRevitContextWriter _contextWriter;
    private Action<UIApplication>? _pendingAction;
    private ExternalEvent? _ownedEvent;

    public PipeConnectExternalEvent(IRevitContextWriter contextWriter)
    {
        _contextWriter = contextWriter;
    }

    public void Initialize(ExternalEvent revitEvent)
    {
        _ownedEvent = revitEvent;
    }

    public void Raise(Action<UIApplication> action)
    {
        Interlocked.Exchange(ref _pendingAction, action ?? throw new ArgumentNullException(nameof(action)));
        _ownedEvent?.Raise();
    }

    public void Enqueue(ExternalEvent externalEvent, Action<UIApplication> action)
    {
        Interlocked.Exchange(ref _pendingAction, action ?? throw new ArgumentNullException(nameof(action)));
        externalEvent.Raise();
    }

    public void Execute(UIApplication app)
    {
        _contextWriter.SetContext(app);

        var action = Interlocked.Exchange(ref _pendingAction, null);
        try
        {
            action?.Invoke(app);
        }
        finally
        {
        }
    }

    public string GetName() => "SmartCon.PipeConnectEvent";
}

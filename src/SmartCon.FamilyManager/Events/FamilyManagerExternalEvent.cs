using System.Threading;
using Autodesk.Revit.UI;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Events;

public sealed class FamilyManagerExternalEvent : IExternalEventHandler, IFamilyManagerExternalEvent
{
    private readonly IRevitContextWriter _contextWriter;
    private Action<UIApplication>? _pendingAction;
    private ExternalEvent? _ownedEvent;

    public FamilyManagerExternalEvent(IRevitContextWriter contextWriter)
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

    public void Raise(Action action)
    {
        Raise(_ => action());
    }

    public void Execute(UIApplication app)
    {
        _contextWriter.SetContext(app);
        var action = Interlocked.Exchange(ref _pendingAction, null);
        action?.Invoke(app);
    }

    public string GetName() => "SmartCon.FamilyManagerEvent";
}

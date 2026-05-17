using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Events;

/// <summary>
/// ExternalEvent handler с ConcurrentQueue для надежной обработки множественных Raise().
/// Паттерн: process all actions in one external event context (Autodesk best practice).
/// </summary>
public sealed class FamilyManagerExternalEvent : IExternalEventHandler, IFamilyManagerExternalEvent
{
    private readonly IRevitContextWriter _contextWriter;
    private readonly ConcurrentQueue<Action<UIApplication>> _actionQueue = new();
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
        if (_ownedEvent is null)
            throw new InvalidOperationException("FamilyManagerExternalEvent not initialized. Call Initialize() first.");

        _actionQueue.Enqueue(action ?? throw new ArgumentNullException(nameof(action)));
        _ownedEvent.Raise();
    }

    public void Raise(Action action)
    {
        Raise(_ => action());
    }

    public void RaiseWithApplication(Action<object> actionWithApp)
    {
        Raise(app => actionWithApp(app));
    }

    public void Execute(UIApplication app)
    {
        _contextWriter.SetContext(app);
        while (_actionQueue.TryDequeue(out var action))
        {
            action?.Invoke(app);
        }
    }

    public string GetName() => "SmartCon.FamilyManagerEvent";
}

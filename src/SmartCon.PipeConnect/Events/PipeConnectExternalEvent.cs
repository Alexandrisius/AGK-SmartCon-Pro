using Autodesk.Revit.UI;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.PipeConnect.Events;

/// <summary>
/// Выделенный IExternalEventHandler для операций PipeConnect (Phase 2+, ADR-008).
/// Поддерживает два режима:
/// — Enqueue(ExternalEvent, action): явная передача ExternalEvent (Phase 2).
/// — Raise(action): использует внутренний ExternalEvent, инициализированный через Initialize() (Phase 8).
/// </summary>
public sealed class PipeConnectExternalEvent : IExternalEventHandler
{
    private readonly IRevitContextWriter _contextWriter;
    private volatile Action<UIApplication>? _pendingAction;
    private ExternalEvent? _ownedEvent;

    public PipeConnectExternalEvent(IRevitContextWriter contextWriter)
    {
        _contextWriter = contextWriter;
    }

    /// <summary>
    /// Инициализировать внутренний ExternalEvent. Вызывается из ServiceRegistrar после Create().
    /// </summary>
    public void Initialize(ExternalEvent revitEvent)
    {
        _ownedEvent = revitEvent;
    }

    /// <summary>
    /// Поднять ExternalEvent с внутренним _ownedEvent. Требует предварительного Initialize().
    /// Вызывать из WPF UI-потока (I-01).
    /// </summary>
    public void Raise(Action<UIApplication> action)
    {
        _pendingAction = action ?? throw new ArgumentNullException(nameof(action));
        _ownedEvent?.Raise();
    }

    /// <summary>
    /// Поставить действие в очередь через явный ExternalEvent (Phase 2 backward compat).
    /// </summary>
    public void Enqueue(ExternalEvent externalEvent, Action<UIApplication> action)
    {
        _pendingAction = action ?? throw new ArgumentNullException(nameof(action));
        externalEvent.Raise();
    }

    public void Execute(UIApplication app)
    {
        _contextWriter.SetContext(app);

        try
        {
            _pendingAction?.Invoke(app);
        }
        finally
        {
            _pendingAction = null;
        }
    }

    public string GetName() => "SmartCon.PipeConnectEvent";
}

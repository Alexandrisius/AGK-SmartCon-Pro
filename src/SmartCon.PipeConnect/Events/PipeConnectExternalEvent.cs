using Autodesk.Revit.UI;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.PipeConnect.Events;

/// <summary>
/// Выделенный IExternalEventHandler для операций PipeConnect (выравнивание, ConnectTo).
/// Используется WPF-слоем (Phase 6+) для вызова Revit API из UI-потока (I-01, ADR-008).
/// В Phase 2 операции выполняются напрямую из PipeConnectCommand.Execute().
/// </summary>
public sealed class PipeConnectExternalEvent : IExternalEventHandler
{
    private readonly IRevitContextWriter _contextWriter;
    private volatile Action<UIApplication>? _pendingAction;

    public PipeConnectExternalEvent(IRevitContextWriter contextWriter)
    {
        _contextWriter = contextWriter;
    }

    /// <summary>
    /// Поставить действие в очередь и поднять ExternalEvent.
    /// Вызывать только из WPF UI-потока.
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

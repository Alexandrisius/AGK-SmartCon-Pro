using Autodesk.Revit.UI;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Events;

/// <summary>
/// Универсальный Action Queue handler для ExternalEvent (ADR-008).
/// ViewModel записывает Action перед вызовом Raise(), handler выполняет её в Revit main thread.
/// При каждом Execute() обновляет RevitContext через IRevitContextWriter.
/// </summary>
public sealed class ActionExternalEventHandler : IExternalEventHandler
{
    private readonly IRevitContextWriter _contextWriter;
    private volatile Action<UIApplication>? _pendingAction;

    public ActionExternalEventHandler(IRevitContextWriter contextWriter)
    {
        _contextWriter = contextWriter;
    }

    /// <summary>
    /// Записать действие и поднять ExternalEvent.
    /// Вызывать из WPF UI thread.
    /// </summary>
    public void Raise(ExternalEvent externalEvent, Action<UIApplication> action)
    {
        _pendingAction = action;
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

    public string GetName() => "SmartCon.ActionHandler";
}

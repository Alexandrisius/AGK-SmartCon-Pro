using System.Threading;
using Autodesk.Revit.UI;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Events;

public sealed class ActionExternalEventHandler : IExternalEventHandler
{
    private readonly IRevitContextWriter _contextWriter;
    private Action<UIApplication>? _pendingAction;

    public ActionExternalEventHandler(IRevitContextWriter contextWriter)
    {
        _contextWriter = contextWriter;
    }

    public void Raise(ExternalEvent externalEvent, Action<UIApplication> action)
    {
        Interlocked.Exchange(ref _pendingAction, action);
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

    public string GetName() => "SmartCon.ActionHandler";
}

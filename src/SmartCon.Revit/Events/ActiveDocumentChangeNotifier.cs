using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Events;

/// <summary>
/// Revit-adapter implementing <see cref="IActiveDocumentChangeNotifier"/>.
/// Subscribes to <c>UIControlledApplication.ViewActivated</c> (decision A2 of
/// #119 — recommended by Jeremy Tammik since it fires on both DocumentOpened
/// and cross-document tab switches) and forwards the active file path to
/// subscribers after filtering out unsaved / detached / family documents
/// (decision A4: <c>Document.PathName == ""</c> covers unsaved AND detached,
/// per the official API docs).
/// </summary>
/// <remarks>
/// The notifier is registered in <c>ServiceRegistrar</c> as a singleton. Its
/// <see cref="Register"/> method is called once from <c>App.OnStartup</c>
/// (which holds the <see cref="UIControlledApplication"/> reference) and the
/// <see cref="Dispose"/> method on shutdown — see <c>ServiceLocator.Dispose</c>
/// path wired in <c>App.OnShutdown</c>. We must subscribe on
/// <c>UIControlledApplication</c> rather than <c>UIApplication</c> because
/// the latter is only available inside <c>ExternalEvent</c> callbacks, while
/// <c>ViewActivated</c> needs to fire from start-up. Per Exa research
/// (Autodesk forum thread p/12528359), <c>ViewActivated</c> may even fire
/// *before* <c>ApplicationInitialized</c> when the user starts Revit by
/// double-clicking a file — our <see cref="OnViewActivated"/> filter
/// tolerates that by skipping when no notifier has been registered yet.
/// </remarks>
public sealed class ActiveDocumentChangeNotifier : IActiveDocumentChangeNotifier, IDisposable
{
    private UIControlledApplication? _application;

    public event EventHandler<ActiveDocumentChangedEventArgs>? ActiveDocumentChanged;

    /// <summary>
    /// Subscribe to <c>ViewActivated</c>. Must be called exactly once from
    /// <c>App.OnStartup</c> while the <see cref="UIControlledApplication"/>
    /// reference is available.
    /// </summary>
    public void Register(UIControlledApplication application)
    {
        _application = application;
        application.ViewActivated += OnViewActivated;
    }

    /// <summary>
    /// Unsubscribe from <c>ViewActivated</c>. Idempotent — calling
    /// <see cref="Dispose"/> more than once is safe.
    /// </summary>
    public void Dispose()
    {
        if (_application is not null)
        {
            _application.ViewActivated -= OnViewActivated;
            _application = null;
        }
    }

    private void OnViewActivated(object? sender, ViewActivatedEventArgs e)
    {
        using var _scope = SmartConLogger.BeginScope("FMActiveDoc",
            ("Method", nameof(OnViewActivated)));

        if (e.Status != RevitAPIEventStatus.Succeeded)
        {
            SmartConLogger.Debug($"ViewActivated status={e.Status} — ignoring");
            return;
        }

        var currentDoc = e.CurrentActiveView?.Document;
        if (currentDoc is null)
        {
            SmartConLogger.Debug("CurrentActiveView.Document is null — ignoring");
            return;
        }

        if (currentDoc.IsFamilyDocument)
        {
            SmartConLogger.Debug("Active document is a family (.rfa) — ignoring, no project-base auto-activation");
            return;
        }

        if (string.IsNullOrEmpty(currentDoc.PathName))
        {
            SmartConLogger.Debug("Active document has empty PathName (unsaved/detached) — ignoring per #119 A4");
            return;
        }

        var previous = e.PreviousActiveView?.Document;
        if (previous is not null && previous.Equals(currentDoc))
        {
            SmartConLogger.Debug("Previous and current document are the same (intra-project view switch) — ignoring");
            return;
        }

        SmartConLogger.Info($"Active document changed to '{System.IO.Path.GetFileName(currentDoc.PathName)}'");
        ActiveDocumentChanged?.Invoke(this, new ActiveDocumentChangedEventArgs(currentDoc.PathName));
    }
}
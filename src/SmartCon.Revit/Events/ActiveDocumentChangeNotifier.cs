using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Events;

/// <summary>
/// Revit-adapter implementing <see cref="IActiveDocumentChangeNotifier"/>.
/// Subscribes to <c>UIControlledApplication.ViewActivated</c> (decision A2 of
/// #119 — recommended by Jeremy Tammik since it fires on both DocumentOpened
/// and cross-document tab switches) and to <c>ControlledApplication.DocumentSaved</c>
/// / <c>DocumentSavedAs</c> (Issue #128). Forwards the active file path to
/// subscribers, filtering out family documents and failed / cancelled /
/// non-active save operations. Unsaved documents (empty <c>PathName</c>) are
/// notified with an **empty path** so subscribers can block project bases
/// until the file is saved (#174).
/// </summary>
/// <remarks>
/// The notifier is registered in <c>ServiceRegistrar</c> as a singleton via a
/// DI factory (so it receives <c>IMiniProjectMarker</c>, #188); the factory
/// calls <see cref="Register"/> with the <see cref="UIControlledApplication"/>
/// reference, and the eager resolve in <c>App.OnStartupCore</c> makes the
/// subscription timing explicit (adversarial review M2). <see cref="Dispose"/>
/// runs on shutdown via the DI container — see <c>ServiceLocator.Dispose</c>
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
    private readonly IMiniProjectMarker _miniProjectMarker;
    private UIControlledApplication? _application;
    private Document? _lastActiveDocument;
    private string? _lastNotifiedPath;

    public ActiveDocumentChangeNotifier(IMiniProjectMarker miniProjectMarker)
    {
        _miniProjectMarker = miniProjectMarker;
    }

    public event EventHandler<ActiveDocumentChangedEventArgs>? ActiveDocumentChanged;
    public event EventHandler<ActiveDocumentPathChangedEventArgs>? ActiveDocumentPathChanged;

    /// <summary>
    /// Subscribe to application events. Must be called exactly once from
    /// <c>App.OnStartup</c> while the <see cref="UIControlledApplication"/>
    /// reference is available.
    /// </summary>
    public void Register(UIControlledApplication application)
    {
        _application = application;
        application.ViewActivated += OnViewActivated;
        application.ControlledApplication.DocumentSaved += OnDocumentSaved;
        application.ControlledApplication.DocumentSavedAs += OnDocumentSavedAs;
    }

    /// <summary>
    /// Unsubscribe from all application events. Idempotent — calling
    /// <see cref="Dispose"/> more than once is safe.
    /// </summary>
    public void Dispose()
    {
        if (_application is not null)
        {
            _application.ViewActivated -= OnViewActivated;
            _application.ControlledApplication.DocumentSaved -= OnDocumentSaved;
            _application.ControlledApplication.DocumentSavedAs -= OnDocumentSavedAs;
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

        _lastActiveDocument = currentDoc;
        SmartConLogger.Debug($"Last active document updated (PathName='{GetFileNameOrEmpty(currentDoc.PathName)}')");

        if (currentDoc.IsFamilyDocument)
        {
            SmartConLogger.Debug("Active document is a family (.rfa) — ignoring, no project-base auto-activation");
            return;
        }

        // Intra-project view switch (the most common case) first: it must
        // NOT pay for the ES mini-project read below (audit B3 perf finding).
        var previous = e.PreviousActiveView?.Document;
        if (previous is not null && previous.Equals(currentDoc))
        {
            SmartConLogger.Debug("Previous and current document are the same (intra-project view switch) — ignoring");
            return;
        }

        // #188: a SmartCon reference mini-project (staged system family types)
        // is not a user work project and must never drive active-DB
        // auto-switching — otherwise opening it via "Редактировать" silently
        // switches the catalog to the default base and the curator reimports
        // the new version into the WRONG database. ES marker is authoritative;
        // the path pattern is the fallback for legacy files without a marker.
        if (_miniProjectMarker.IsMiniProject(currentDoc)
            || MiniProjectPathPattern.IsMiniProjectPath(currentDoc.PathName))
        {
            SmartConLogger.Debug("Active document is a SmartCon mini-project (system family reference) — ignoring, current base kept");
            return;
        }

        if (string.IsNullOrEmpty(currentDoc.PathName))
        {
            // #174: an unsaved document (empty PathName, e.g. "Проект1") cannot
            // match any project base. Do NOT ignore it — that would keep the
            // previous document's project base active on a stale path. Notify
            // with an empty path instead so subscribers block project bases
            // and fall back to a general base until the file is saved.
            SmartConLogger.Debug("Active document has empty PathName (unsaved/detached) — notifying with empty path");
            // #174: reset so the first Save/SaveAs of this document always
            // fires — even when it reuses the path of the previously
            // notified document (dedup must not swallow that legitimate Save).
            _lastNotifiedPath = null;
            ActiveDocumentChanged?.Invoke(this, new ActiveDocumentChangedEventArgs(string.Empty));
            return;
        }

        var fileName = System.IO.Path.GetFileName(currentDoc.PathName);
        SmartConLogger.Info($"Active document changed to '{fileName}'");
        _lastNotifiedPath = currentDoc.PathName;
        ActiveDocumentChanged?.Invoke(this, new ActiveDocumentChangedEventArgs(currentDoc.PathName));
        ActiveDocumentPathChanged?.Invoke(this, new ActiveDocumentPathChangedEventArgs(currentDoc.PathName, ActiveDocumentPathChangeReason.Activated));
    }

    private void OnDocumentSaved(object? sender, DocumentSavedEventArgs e)
    {
        HandleDocumentPathChanged(e.Document, e.Status, ActiveDocumentPathChangeReason.Saved, nameof(OnDocumentSaved));
    }

    private void OnDocumentSavedAs(object? sender, DocumentSavedAsEventArgs e)
    {
        HandleDocumentPathChanged(e.Document, e.Status, ActiveDocumentPathChangeReason.SavedAs, nameof(OnDocumentSavedAs));
    }

    private void HandleDocumentPathChanged(Document doc, RevitAPIEventStatus status, ActiveDocumentPathChangeReason reason, string methodName)
    {
        using var _scope = SmartConLogger.BeginScope("FMActiveDoc",
            ("Method", methodName));

        if (status != RevitAPIEventStatus.Succeeded)
        {
            SmartConLogger.Debug($"Save event status={status} — ignoring");
            return;
        }

        if (doc.IsFamilyDocument)
        {
            SmartConLogger.Debug("Saved document is a family (.rfa) — ignoring");
            return;
        }

        // #188: manual Ctrl+S inside a mini-project (blocked on disk by the
        // read-only attribute, I-16) or a programmatic SaveAs of a staged file
        // must not trigger project-base re-activation either.
        if (_miniProjectMarker.IsMiniProject(doc)
            || MiniProjectPathPattern.IsMiniProjectPath(doc.PathName))
        {
            SmartConLogger.Debug("Saved document is a SmartCon mini-project — ignoring, current base kept");
            return;
        }

        if (string.IsNullOrEmpty(doc.PathName))
        {
            SmartConLogger.Debug("Saved document has empty PathName — ignoring");
            return;
        }

        if (_lastActiveDocument is null || !_lastActiveDocument.IsValidObject || !_lastActiveDocument.Equals(doc))
        {
            SmartConLogger.Debug($"Saved document '{GetFileNameOrEmpty(doc.PathName)}' is not the current active document — ignoring");
            return;
        }

        if (string.Equals(_lastNotifiedPath, doc.PathName, StringComparison.OrdinalIgnoreCase))
        {
            SmartConLogger.Debug($"Saved document path '{System.IO.Path.GetFileName(doc.PathName)}' already notified — ignoring duplicate");
            return;
        }

        var fileName = System.IO.Path.GetFileName(doc.PathName);
        SmartConLogger.Info($"Active document path changed to '{fileName}' via {reason}");
        _lastNotifiedPath = doc.PathName;
        ActiveDocumentPathChanged?.Invoke(this, new ActiveDocumentPathChangedEventArgs(doc.PathName, reason));
    }

    private static string GetFileNameOrEmpty(string? path)
    {
        return string.IsNullOrEmpty(path) ? "(empty)" : System.IO.Path.GetFileName(path);
    }
}

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Cross-module notifier for "the active Revit document changed or received a
/// file path". The Revit layer (<c>SmartCon.Revit</c>) subscribes to
/// <c>UIControlledApplication.ViewActivated</c> and
/// <c>ControlledApplication.DocumentSaved</c>/<c>DocumentSavedAs</c>, then
/// raises these events after filtering out family documents and failed/cancelled
/// save operations (decision A4, see #119, and Issue #128). Subscribers include
/// the FamilyManager ViewModel which uses it to drive project-base auto-activation.
/// </summary>
public interface IActiveDocumentChangeNotifier
{
    /// <summary>
    /// Raised on a non-family Revit document becoming active. The argument is
    /// the new active file path, or an **empty** path when the active document
    /// is unsaved (no <c>PathName</c>, #174) — subscribers must treat that as
    /// "no file name to evaluate yet" (project bases are blocked until the
    /// file is saved).
    /// </summary>
    event EventHandler<ActiveDocumentChangedEventArgs>? ActiveDocumentChanged;

    /// <summary>
    /// Raised when the active document's file path changes while the document
    /// stays active — typically after <c>SaveAs</c> or the first save of a
    /// newly created project. The argument is the new file path and the reason
    /// for the change.
    /// </summary>
    event EventHandler<ActiveDocumentPathChangedEventArgs>? ActiveDocumentPathChanged;
}

/// <summary>Event payload for <see cref="IActiveDocumentChangeNotifier.ActiveDocumentChanged"/>.</summary>
public sealed record ActiveDocumentChangedEventArgs(string FilePath);

/// <summary>Reason why the active document's path was reported.</summary>
public enum ActiveDocumentPathChangeReason
{
    /// <summary>The document became active via the Revit <c>ViewActivated</c> event.</summary>
    Activated,

    /// <summary>The document was saved without changing its path.</summary>
    Saved,

    /// <summary>The document was saved with a new path (first save or SaveAs).</summary>
    SavedAs
}

/// <summary>Event payload for <see cref="IActiveDocumentChangeNotifier.ActiveDocumentPathChanged"/>.</summary>
public sealed record ActiveDocumentPathChangedEventArgs(string FilePath, ActiveDocumentPathChangeReason Reason);
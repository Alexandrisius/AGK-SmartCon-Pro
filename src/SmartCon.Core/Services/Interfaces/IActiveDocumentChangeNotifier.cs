namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Cross-module notifier for "the active Revit document changed". The Revit
/// layer (<c>SmartCon.Revit</c>) subscribes to <c>UIControlledApplication.ViewActivated</c>
/// and raises this event after filtering out unsaved/detached/family documents
/// (decision A4, see #119). Subscribers include the FamilyManager ViewModel
/// which uses it to drive project-base auto-activation.
/// </summary>
public interface IActiveDocumentChangeNotifier
{
    /// <summary>
    /// Raised on a non-family, saved Revit document becoming active. The
    /// argument is the new active file path (guaranteed non-empty per the
    /// filter rules in <c>ActiveDocumentChangeNotifier</c>).
    /// </summary>
    event EventHandler<ActiveDocumentChangedEventArgs>? ActiveDocumentChanged;
}

/// <summary>Event payload for <see cref="IActiveDocumentChangeNotifier.ActiveDocumentChanged"/>.</summary>
public sealed record ActiveDocumentChangedEventArgs(string FilePath);
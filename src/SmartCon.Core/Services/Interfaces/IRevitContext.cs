using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Access to the current Document. Do not cache — request on every operation (I-05).
/// UIDocument is not exposed in Core (I-09: Autodesk.Revit.UI prohibition).
/// Use IElementSelectionService for selection.
/// </summary>
public interface IRevitContext
{
    /// <summary>Active Revit document. Do not cache between operations (I-05).</summary>
    /// <exception cref="InvalidOperationException">Revit context is not initialized
    /// (SetContext was never called from an ExternalEvent handler).</exception>
    /// <remarks>In the zero-document state (start page — no document open)
    /// <c>ActiveUIDocument</c> is null and this surfaces as
    /// <see cref="NullReferenceException"/> — call <see cref="TryGetDocument"/>
    /// instead wherever the zero-document state is reachable (#219).</remarks>
    Document GetDocument();

    /// <summary>
    /// Active Revit document, or <c>null</c> in the zero-document state (start
    /// page / all documents closed) — the non-throwing counterpart of
    /// <see cref="GetDocument"/> (#219: <c>ActiveUIDocument</c> is null there,
    /// so <see cref="GetDocument"/> cannot honor a "<c>null</c> when no document"
    /// contract and its callers' null-guards were dead code).
    /// </summary>
    Document? TryGetDocument();

    /// <summary>Revit version string (e.g. "2025").</summary>
    string GetRevitVersion();

    /// <summary>Revit application username (cached at context initialization).</summary>
    string GetUsername();
}

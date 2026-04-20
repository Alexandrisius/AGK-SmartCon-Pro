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
    Document GetDocument();

    /// <summary>Revit version string (e.g. "2025").</summary>
    string GetRevitVersion();
}

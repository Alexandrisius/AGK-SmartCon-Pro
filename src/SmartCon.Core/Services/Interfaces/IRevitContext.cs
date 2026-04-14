using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Access to the current Document. Do not cache — request on every operation (I-05).
/// UIDocument is not exposed in Core (I-09: Autodesk.Revit.UI prohibition).
/// Use IElementSelectionService for selection.
/// </summary>
public interface IRevitContext
{
    Document GetDocument();
    string GetRevitVersion();
}

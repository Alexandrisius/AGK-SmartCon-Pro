using SmartCon.Core.Models;

namespace SmartCon.PipeConnect.Views;

/// <summary>
/// Gives the ViewModel access to the editor window's on-screen bounds in physical
/// pixels so the "Обзор" zoom can compensate for the window occluding the Revit view.
/// Implemented by PipeConnectEditorView (view-side concern, like PositionNearCursor).
/// </summary>
public interface IEditorWindowBoundsAccessor
{
    /// <summary>Window bounds in physical screen pixels (DPI-converted). Null when not shown.</summary>
    ScreenRect? GetWindowBoundsInPixels();
}

using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Programmatic view navigation (zoom/pan) without user input.
/// Safe to call from a modal command context (PipeConnectEditor, I-01a):
/// UIView zoom methods never start a transaction.
/// </summary>
public interface IViewNavigationService
{
    /// <summary>
    /// Zoom the active graphical view so that <paramref name="point"/> ends up centered
    /// in the part of the view NOT occluded by <paramref name="occludingWindow"/>
    /// (our own modal dialog), showing roughly <paramref name="radiusFeet"/> of model
    /// around it. When there is no occlusion the point is centered in the whole view.
    /// </summary>
    /// <param name="point">Target point in world coordinates (internal units, I-02).</param>
    /// <param name="radiusFeet">Half of the desired visible model size (internal units).</param>
    /// <param name="occludingWindow">Screen-pixel bounds of the dialog covering the view, or null.</param>
    ZoomToPointResult ZoomToPoint(XYZ point, double radiusFeet, ScreenRect? occludingWindow);

    /// <summary>
    /// Scale the current zoom of the active graphical view by <paramref name="factor"/>
    /// (&lt;1 zooms in, &gt;1 zooms out) around the center of the visible
    /// (non-occluded) zone, so repeated zooming does not drift behind the dialog.
    /// </summary>
    ZoomToPointResult ZoomByFactor(double factor, ScreenRect? occludingWindow);
}

public enum ZoomToPointResult
{
    Success,
    NoActiveGraphicalView,
    UIViewNotFound,
    DegenerateViewRect
}

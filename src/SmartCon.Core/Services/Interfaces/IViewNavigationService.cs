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

    /// <summary>
    /// Scale the current zoom by <paramref name="factor"/> (accumulates like
    /// <see cref="ZoomByFactor"/>) AND re-center on <paramref name="point"/> in a
    /// single zoom operation — the zoom ± buttons use this so they always track the
    /// active connector without resetting the zoom level or double-zoom flicker.
    /// </summary>
    /// <param name="factor">&lt;1 zooms in, &gt;1 zooms out (relative to current zoom).</param>
    /// <param name="point">Target point in world coordinates (internal units, I-02).</param>
    /// <param name="occludingWindow">Screen-pixel bounds of the dialog covering the view, or null.</param>
    ZoomToPointResult ZoomByFactorToPoint(double factor, XYZ point, ScreenRect? occludingWindow);
}

public enum ZoomToPointResult
{
    Success,
    NoActiveGraphicalView,
    UIViewNotFound,
    DegenerateViewRect
}

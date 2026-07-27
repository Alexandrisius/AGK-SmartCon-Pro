using SmartCon.Core.Models;

namespace SmartCon.Core.Services.PipeConnect;

/// <summary>
/// Result of <see cref="ViewZoomMath.Compute"/> / <see cref="ViewZoomMath.ComputeScaled"/>:
/// the zoom rectangle in view-plane coordinates (u along View.RightDirection,
/// v along View.UpDirection) that UIView.ZoomAndCenterRectangle should receive so
/// the point of interest lands in the center of the visible (non-occluded) zone
/// instead of behind the modal dialog.
/// </summary>
public sealed record ViewZoomRect(double CenterU, double CenterV, double HalfWidth, double HalfHeight, bool UsedFallback);

/// <summary>
/// Pure math for the PipeConnectEditor view navigation ("Просмотр" / zoom ± buttons).
/// Computes zoom rectangles for UIView.ZoomAndCenterRectangle that compensate for
/// the modal dialog occluding part of the Revit view window: ZoomAndCenterRectangle
/// places the rect center at the view center, so the rect center is offset AWAY
/// from the dialog by the same screen distance that separates the view center from
/// the visible-zone center. All computations are 2D on the view plane (I-09).
/// </summary>
public static class ViewZoomMath
{
    /// <summary>Minimum visible zone size in px; below this the occlusion compensation is pointless.</summary>
    public const double MinVisiblePx = 150;

    /// <summary>
    /// Largest axis-aligned sub-rectangle of <paramref name="view"/> not covered by
    /// <paramref name="occlusion"/>. Candidates are the four slabs (left/right/top/bottom)
    /// around the occluding rect; the max-area non-degenerate one wins.
    /// Falls back to the full view rect when there is no occlusion, no intersection,
    /// or the best slab is smaller than <see cref="MinVisiblePx"/> in either dimension.
    /// </summary>
    public static ScreenRect ComputeVisibleRect(ScreenRect view, ScreenRect? occlusion)
    {
        if (occlusion is null || !view.IntersectsWith(occlusion))
            return view;

        var candidates = new[]
        {
            new ScreenRect(view.Left, view.Top, occlusion.Left, view.Bottom),
            new ScreenRect(occlusion.Right, view.Top, view.Right, view.Bottom),
            new ScreenRect(view.Left, view.Top, view.Right, occlusion.Top),
            new ScreenRect(view.Left, occlusion.Bottom, view.Right, view.Bottom)
        };

        ScreenRect? best = null;
        var bestArea = 0.0;
        foreach (var c in candidates)
        {
            if (c.Width <= 0 || c.Height <= 0) continue;
            var area = c.Width * c.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = c;
            }
        }

        if (best is null || best.Width < MinVisiblePx || best.Height < MinVisiblePx)
            return view;

        return best;
    }

    /// <summary>
    /// Computes the zoom rectangle for a target point and radius ("Просмотр" button).
    /// <paramref name="targetU"/>/<paramref name="targetV"/> are the target point in
    /// view-plane coordinates (any origin — the result uses the same origin).
    /// Returns null for a degenerate view rect.
    /// </summary>
    public static ViewZoomRect? Compute(
        double targetU, double targetV, double radiusModel,
        ScreenRect view, ScreenRect? occlusion)
    {
        if (view.Width <= 0 || view.Height <= 0 || radiusModel <= 0)
            return null;

        var visible = ComputeVisibleRect(view, occlusion);
        var usedFallback = IsFallback(view, occlusion, visible);

        var dPxX = view.CenterX - visible.CenterX;
        var dPxY = view.CenterY - visible.CenterY;

        // The visible zone must show `radiusModel` around the target; the full view
        // is wider/taller than the visible zone by the pixel ratio, so the zoom rect
        // (which fills the FULL view) is scaled up by the same ratio.
        var fullW = 2.0 * radiusModel * view.Width / visible.Width;
        var fullH = 2.0 * radiusModel * view.Height / visible.Height;

        var scaleX = fullW / view.Width;
        var scaleY = fullH / view.Height;

        // Screen Y points down, view-plane v points up — hence the minus for dv.
        var du = dPxX * scaleX;
        var dv = -dPxY * scaleY;

        return new ViewZoomRect(targetU + du, targetV + dv, fullW / 2.0, fullH / 2.0, usedFallback);
    }

    /// <summary>
    /// Computes the zoom rectangle for the zoom ± buttons: scales the CURRENT view
    /// rectangle by <paramref name="factor"/> (&lt;1 zooms in, &gt;1 zooms out) so the
    /// model point currently shown at the center of the visible zone STAYS there —
    /// repeated zooming does not drift the point of interest behind the dialog.
    /// The rect center converges toward the visible-zone point as the zoom level grows.
    /// <paramref name="currentU0"/>..<paramref name="currentV1"/> are the current
    /// GetZoomCorners in view-plane coordinates (same origin as the result).
    /// </summary>
    public static ViewZoomRect? ComputeScaled(
        double currentU0, double currentV0, double currentU1, double currentV1,
        double factor, ScreenRect view, ScreenRect? occlusion)
    {
        var w = currentU1 - currentU0;
        var h = currentV1 - currentV0;
        if (view.Width <= 0 || view.Height <= 0 || w <= 0 || h <= 0 || factor <= 0)
            return null;

        var visible = ComputeVisibleRect(view, occlusion);
        var usedFallback = IsFallback(view, occlusion, visible);

        var dPxX = view.CenterX - visible.CenterX;
        var dPxY = view.CenterY - visible.CenterY;

        // The model point shown at the visible-zone center is offset from the view
        // center by dPx at the CURRENT scale; the new rect center is offset by dPx
        // at the NEW scale (same anchoring as in Compute, both scales differ).
        var scaleCurX = w / view.Width;
        var scaleCurY = h / view.Height;
        var scaleNewX = w * factor / view.Width;
        var scaleNewY = h * factor / view.Height;

        var centerU = (currentU0 + currentU1) / 2.0 + dPxX * (scaleNewX - scaleCurX);
        var centerV = (currentV0 + currentV1) / 2.0 + dPxY * (scaleCurY - scaleNewY);

        return new ViewZoomRect(centerU, centerV, w * factor / 2.0, h * factor / 2.0, usedFallback);
    }

    private static bool IsFallback(ScreenRect view, ScreenRect? occlusion, ScreenRect visible)
        => occlusion is not null && view.IntersectsWith(occlusion)
            && visible.Left == view.Left && visible.Top == view.Top
            && visible.Right == view.Right && visible.Bottom == view.Bottom;
}

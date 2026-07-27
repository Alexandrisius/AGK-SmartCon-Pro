using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.PipeConnect;
using SmartCon.Revit.Context;

namespace SmartCon.Revit.Navigation;

/// <summary>
/// IViewNavigationService implementation: zooms the active graphical view via
/// UIView.ZoomAndCenterRectangle so the point of interest lands in the visible
/// (non-dialog-occluded) zone. UIView zoom methods never start a transaction,
/// so this is safe from the PipeConnectEditor modal command context (I-01a).
/// </summary>
public sealed class RevitViewNavigationService : IViewNavigationService
{
    private readonly IRevitUIContext _uiContext;

    public RevitViewNavigationService(IRevitUIContext uiContext)
    {
        _uiContext = uiContext;
    }

    public ZoomToPointResult ZoomToPoint(XYZ point, double radiusFeet, ScreenRect? occludingWindow)
    {
        using var _scope = SmartConLogger.BeginScope("Nav",
            ("Method", nameof(ZoomToPoint)),
            ("RadiusFt", radiusFeet.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));

        if (!TryGetViewFrame(out var uidoc, out var view, out var uiView, out var viewPx, out var result))
            return result;

        var frame = GetViewPlaneFrame(view!, uiView!);
        var targetU = point.DotProduct(frame.Right);
        var targetV = point.DotProduct(frame.Up);

        var zoom = ViewZoomMath.Compute(targetU, targetV, radiusFeet, viewPx, occludingWindow);
        if (zoom is null)
        {
            SmartConLogger.Warn($"Degenerate view rect {viewPx.Width}x{viewPx.Height} [Action: разверните окно Revit и повторите «Просмотр»]");
            return ZoomToPointResult.DegenerateViewRect;
        }

        ApplyZoom(uidoc!, uiView!, frame, zoom);
        SmartConLogger.Info($"Zoomed to point, view '{view!.Name}' occlusion={(occludingWindow is not null)} fallback={zoom.UsedFallback}");
        return ZoomToPointResult.Success;
    }

    public ZoomToPointResult ZoomByFactor(double factor, ScreenRect? occludingWindow)
    {
        using var _scope = SmartConLogger.BeginScope("Nav",
            ("Method", nameof(ZoomByFactor)),
            ("Factor", factor.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));

        if (!TryGetViewFrame(out var uidoc, out var view, out var uiView, out var viewPx, out var result))
            return result;

        var frame = GetViewPlaneFrame(view!, uiView!);
        var u0 = frame.Corner0.DotProduct(frame.Right);
        var v0 = frame.Corner0.DotProduct(frame.Up);
        var u1 = frame.Corner1.DotProduct(frame.Right);
        var v1 = frame.Corner1.DotProduct(frame.Up);

        var zoom = ViewZoomMath.ComputeScaled(u0, v0, u1, v1, factor, viewPx, occludingWindow);
        if (zoom is null)
        {
            SmartConLogger.Warn($"Degenerate view/corners rect [Action: разверните окно Revit и повторите зум]");
            return ZoomToPointResult.DegenerateViewRect;
        }

        ApplyZoom(uidoc!, uiView!, frame, zoom);
        SmartConLogger.Info($"Zoom x{factor:F2}, view '{view!.Name}' fallback={zoom.UsedFallback}");
        return ZoomToPointResult.Success;
    }

    private bool TryGetViewFrame(
        out UIDocument? uidoc, out View? view, out UIView? uiView,
        out ScreenRect viewPx, out ZoomToPointResult result)
    {
        uidoc = _uiContext.GetUIDocument();
        view = uidoc.ActiveGraphicalView;
        uiView = null;
        viewPx = new ScreenRect(0, 0, 0, 0);
        result = ZoomToPointResult.Success;

        if (view is null)
        {
            SmartConLogger.Warn("No active graphical view [Action: переключитесь на 3D/план/разрез и повторите]");
            result = ZoomToPointResult.NoActiveGraphicalView;
            return false;
        }

        foreach (var uv in uidoc.GetOpenUIViews())
        {
            if (uv.ViewId == view.Id)
            {
                uiView = uv;
                break;
            }
        }
        if (uiView is null)
        {
            SmartConLogger.Warn($"UIView not found for view id={view.Id.GetValue()} [Action: переключитесь на другой вид и обратно, затем повторите]");
            result = ZoomToPointResult.UIViewNotFound;
            return false;
        }

        var winRect = uiView.GetWindowRectangle();
        viewPx = new ScreenRect(winRect.Left, winRect.Top, winRect.Right, winRect.Bottom);
        return true;
    }

    /// <summary>
    /// View-plane frame: u along RightDirection, v along UpDirection, origin anchored
    /// at the current zoom corner so the resulting rect is guaranteed to lie on the
    /// view plane (multiplicative-style arithmetic from current corners — avoids
    /// absolute-coordinate precision issues).
    /// </summary>
    private static ViewPlaneFrame GetViewPlaneFrame(View view, UIView uiView)
    {
        var corners = uiView.GetZoomCorners();
        return new ViewPlaneFrame(view.RightDirection, view.UpDirection, corners[0], corners[1]);
    }

    private static void ApplyZoom(UIDocument uidoc, UIView uiView, ViewPlaneFrame frame, ViewZoomRect zoom)
    {
        var baseU = frame.Corner0.DotProduct(frame.Right);
        var baseV = frame.Corner0.DotProduct(frame.Up);

        XYZ ToWorld(double u, double v)
            => frame.Corner0 + frame.Right * (u - baseU) + frame.Up * (v - baseV);

        var corner1 = ToWorld(zoom.CenterU - zoom.HalfWidth, zoom.CenterV - zoom.HalfHeight);
        var corner2 = ToWorld(zoom.CenterU + zoom.HalfWidth, zoom.CenterV + zoom.HalfHeight);

        // Refresh first so freshly inserted fittings/reducers are rendered before the zoom.
        uidoc.RefreshActiveView();
        uiView.ZoomAndCenterRectangle(corner1, corner2);
    }

    private sealed record ViewPlaneFrame(XYZ Right, XYZ Up, XYZ Corner0, XYZ Corner1);
}

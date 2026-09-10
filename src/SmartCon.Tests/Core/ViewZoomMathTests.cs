using SmartCon.Core.Models;
using SmartCon.Core.Services.PipeConnect;
using Xunit;

namespace SmartCon.Tests.Core;

/// <summary>
/// Tests for <see cref="ViewZoomMath"/> — the PipeConnectEditor view navigation math
/// ("Просмотр" / zoom ±) that compensates for the modal editor window occluding
/// the Revit view. Reference frame: view 1000×500 px, radius 5 model units.
/// </summary>
public sealed class ViewZoomMathTests
{
    private static readonly ScreenRect View = new(0, 0, 1000, 500);
    private const double Radius = 5.0;

    // --- Compute (Inspect button) ---

    [Fact]
    public void NoOcclusion_RectCenteredOnTarget()
    {
        var zoom = ViewZoomMath.Compute(0, 0, Radius, View, null);

        Assert.NotNull(zoom);
        Assert.Equal(0, zoom.CenterU, 9);
        Assert.Equal(0, zoom.CenterV, 9);
        Assert.Equal(Radius, zoom.HalfWidth, 9);   // 2R * 1000/1000 / 2
        Assert.Equal(Radius, zoom.HalfHeight, 9);  // 2R * 500/500 / 2
        Assert.False(zoom.UsedFallback);
    }

    [Fact]
    public void OcclusionNotIntersecting_TreatedAsNoOcclusion()
    {
        var occlusion = new ScreenRect(-500, 0, -100, 300);

        var zoom = ViewZoomMath.Compute(0, 0, Radius, View, occlusion);

        Assert.NotNull(zoom);
        Assert.Equal(0, zoom.CenterU, 9);
        Assert.Equal(0, zoom.CenterV, 9);
        Assert.False(zoom.UsedFallback);
    }

    [Fact]
    public void OcclusionRight_TargetShiftsLeftIntoVisibleZone()
    {
        // Dialog covers the right 40% of the view (600..1000).
        var occlusion = new ScreenRect(600, 0, 1000, 500);

        var zoom = ViewZoomMath.Compute(0, 0, Radius, View, occlusion);

        Assert.NotNull(zoom);
        Assert.True(zoom.CenterU > 0, $"CenterU should move right (target moves left), got {zoom.CenterU}");
        Assert.Equal(0, zoom.CenterV, 9);
        Assert.False(zoom.UsedFallback);

        Assert.Equal(2 * Radius * 1000.0 / 600.0 / 2.0, zoom.HalfWidth, 6);
        var scaleX = zoom.HalfWidth * 2.0 / 1000.0;
        Assert.Equal(200.0 * scaleX, zoom.CenterU, 6);
    }

    [Fact]
    public void OcclusionLeft_TargetShiftsRightIntoVisibleZone()
    {
        var occlusion = new ScreenRect(0, 0, 400, 500);

        var zoom = ViewZoomMath.Compute(0, 0, Radius, View, occlusion);

        Assert.NotNull(zoom);
        Assert.True(zoom.CenterU < 0, $"CenterU should move left (target moves right), got {zoom.CenterU}");
        Assert.Equal(0, zoom.CenterV, 9);
    }

    [Fact]
    public void OcclusionTop_TargetShiftsDownIntoVisibleZone()
    {
        var occlusion = new ScreenRect(0, 0, 1000, 200);

        var zoom = ViewZoomMath.Compute(0, 0, Radius, View, occlusion);

        Assert.NotNull(zoom);
        Assert.True(zoom.CenterV > 0, $"CenterV should move up (target moves down on screen), got {zoom.CenterV}");
        Assert.Equal(0, zoom.CenterU, 9);

        var scaleY = zoom.HalfHeight * 2.0 / 500.0;
        Assert.Equal(100.0 * scaleY, zoom.CenterV, 6);
    }

    [Fact]
    public void OcclusionBottom_TargetShiftsUpIntoVisibleZone()
    {
        var occlusion = new ScreenRect(0, 350, 1000, 500);

        var zoom = ViewZoomMath.Compute(0, 0, Radius, View, occlusion);

        Assert.NotNull(zoom);
        Assert.True(zoom.CenterV < 0, $"CenterV should move down (target moves up on screen), got {zoom.CenterV}");
    }

    [Fact]
    public void OcclusionCorner_PicksLargerSlab()
    {
        // left slab = 700×500 = 350k, top slab = 1000×100 = 100k → left wins.
        var occlusion = new ScreenRect(700, 100, 1000, 500);

        var zoom = ViewZoomMath.Compute(0, 0, Radius, View, occlusion);

        Assert.NotNull(zoom);
        Assert.True(zoom.CenterU > 0, $"Expected horizontal compensation, got U={zoom.CenterU} V={zoom.CenterV}");
        Assert.Equal(0, zoom.CenterV, 9);
    }

    [Fact]
    public void NearTotalOcclusion_FallsBackToFullView()
    {
        var occlusion = new ScreenRect(100, 0, 1000, 500);

        var zoom = ViewZoomMath.Compute(0, 0, Radius, View, occlusion);

        Assert.NotNull(zoom);
        Assert.True(zoom.UsedFallback);
        Assert.Equal(0, zoom.CenterU, 9);
        Assert.Equal(0, zoom.CenterV, 9);
    }

    [Fact]
    public void DegenerateViewRect_ReturnsNull()
    {
        Assert.Null(ViewZoomMath.Compute(0, 0, Radius, new ScreenRect(0, 0, 0, 500), null));
        Assert.Null(ViewZoomMath.Compute(0, 0, Radius, new ScreenRect(0, 0, 1000, 0), null));
        Assert.Null(ViewZoomMath.Compute(0, 0, 0, View, null));
    }

    [Fact]
    public void NonZeroTarget_PreservedRelativeOffset()
    {
        var zoom = ViewZoomMath.Compute(123.4, -56.7, Radius, View, null);

        Assert.NotNull(zoom);
        Assert.Equal(123.4, zoom.CenterU, 9);
        Assert.Equal(-56.7, zoom.CenterV, 9);
    }

    // --- ComputeVisibleRect ---

    [Fact]
    public void ComputeVisibleRect_NoIntersection_ReturnsView()
    {
        var result = ViewZoomMath.ComputeVisibleRect(View, new ScreenRect(2000, 2000, 3000, 3000));
        Assert.Equal(View, result);
    }

    [Fact]
    public void ComputeVisibleRect_CenterOcclusion_PicksLargestSideSlab()
    {
        // Centered dialog 300×200 → side slabs 350×500 (175k) beat top/bottom 1000×150 (150k).
        var result = ViewZoomMath.ComputeVisibleRect(View, new ScreenRect(350, 150, 650, 350));

        Assert.Equal(500, result.Height, 9);
        Assert.True(
            (result.Left == 0 && result.Right == 350) || (result.Left == 650 && result.Right == 1000),
            $"Expected a side slab, got {result}");
    }

    // --- ComputeScaled (zoom ± buttons) ---

    [Fact]
    public void Scaled_NoOcclusion_ScalesAroundCurrentCenter()
    {
        // Current corners: 20×10 model units centered at (4, 2).
        var zoom = ViewZoomMath.ComputeScaled(-6, -3, 14, 7, 0.5, View, null);

        Assert.NotNull(zoom);
        Assert.Equal(4, zoom.CenterU, 9);
        Assert.Equal(2, zoom.CenterV, 9);
        Assert.Equal(5, zoom.HalfWidth, 9);   // 20 * 0.5 / 2
        Assert.Equal(2.5, zoom.HalfHeight, 9);
    }

    [Fact]
    public void Scaled_OcclusionRight_KeepsVisibleZoneAnchored()
    {
        // Dialog covers the right 40%; current corners 20×10 centered at origin.
        var occlusion = new ScreenRect(600, 0, 1000, 500);

        var zoom = ViewZoomMath.ComputeScaled(-10, -5, 10, 5, 0.5, View, occlusion);

        Assert.NotNull(zoom);
        // scaleCur = 20/1000 = 0.02, scaleNew = 10/1000 = 0.01, dPxX = +200
        // center = 0 + 200*(0.01 - 0.02) = -2.
        Assert.Equal(-2.0, zoom.CenterU, 9);
        Assert.Equal(0, zoom.CenterV, 9);
        Assert.Equal(5, zoom.HalfWidth, 9);
        // The visible-zone model point (center - dPx*scaleNew = -2 - 200*0.01 = -4)
        // is the SAME point that was at the visible center before the zoom
        // (0 - 200*0.02 = -4).
    }

    [Fact]
    public void Scaled_ZoomOutFactor_GrowsRect()
    {
        var zoom = ViewZoomMath.ComputeScaled(-10, -5, 10, 5, 2.0, View, null);

        Assert.NotNull(zoom);
        Assert.Equal(20, zoom.HalfWidth, 9);
        Assert.Equal(10, zoom.HalfHeight, 9);
    }

    [Fact]
    public void Scaled_DegenerateInput_ReturnsNull()
    {
        Assert.Null(ViewZoomMath.ComputeScaled(0, 0, 0, 5, 0.5, View, null));   // zero width corners
        Assert.Null(ViewZoomMath.ComputeScaled(0, 0, 10, 5, 0, View, null));    // zero factor
        Assert.Null(ViewZoomMath.ComputeScaled(0, 0, 10, 5, 0.5, new ScreenRect(0, 0, 0, 0), null));
    }

    [Fact]
    public void Scaled_RepeatedZoomIn_VisiblePointDoesNotDrift()
    {
        // Two consecutive zoom-ins with the same occlusion must keep the SAME model
        // point at the visible-zone center — no drift behind the dialog between steps.
        var occlusion = new ScreenRect(600, 0, 1000, 500);

        var first = ViewZoomMath.ComputeScaled(-10, -5, 10, 5, 0.5, View, occlusion);
        Assert.NotNull(first);

        var second = ViewZoomMath.ComputeScaled(
            first.CenterU - first.HalfWidth, first.CenterV - first.HalfHeight,
            first.CenterU + first.HalfWidth, first.CenterV + first.HalfHeight,
            0.5, View, occlusion);

        Assert.NotNull(second);
        // Visible-zone model point = rectCenter - dPxX * scaleNew (u axis).
        double VisiblePointU(ViewZoomRect z) => z.CenterU - 200.0 * (z.HalfWidth * 2.0 / 1000.0);
        Assert.Equal(VisiblePointU(first), VisiblePointU(second), 9);
        Assert.Equal(-4.0, VisiblePointU(first), 9);
        // And the rect center converges toward that point as the zoom grows.
        Assert.True(System.Math.Abs(second.CenterU - (-4.0)) < System.Math.Abs(first.CenterU - (-4.0)));
    }

    // --- ComputeScaledToPoint (zoom ± tracking the active connector) ---

    [Fact]
    public void ScaledToPoint_NoOcclusion_CentersOnTargetKeepsScale()
    {
        // Current corners 20×10 centered at (100, 100); target at origin; factor 0.5.
        var zoom = ViewZoomMath.ComputeScaledToPoint(90, 95, 110, 105, 0.5, 0, 0, View, null);

        Assert.NotNull(zoom);
        Assert.Equal(0, zoom.CenterU, 9);
        Assert.Equal(0, zoom.CenterV, 9);
        Assert.Equal(5, zoom.HalfWidth, 9);    // 20 * 0.5 / 2 — scale from CURRENT corners
        Assert.Equal(2.5, zoom.HalfHeight, 9);
    }

    [Fact]
    public void ScaledToPoint_RepeatedZoomIn_AccumulatesScale()
    {
        // Two consecutive zoom-ins on the SAME target must shrink the rect each time
        // (unlike ZoomToPoint which resets to a fixed radius).
        var first = ViewZoomMath.ComputeScaledToPoint(-10, -5, 10, 5, 0.5, 3, 1, View, null);
        Assert.NotNull(first);

        var second = ViewZoomMath.ComputeScaledToPoint(
            first.CenterU - first.HalfWidth, first.CenterV - first.HalfHeight,
            first.CenterU + first.HalfWidth, first.CenterV + first.HalfHeight,
            0.5, 3, 1, View, null);

        Assert.NotNull(second);
        Assert.Equal(first.HalfWidth / 2.0, second.HalfWidth, 9);
        Assert.Equal(3, second.CenterU, 9);
        Assert.Equal(1, second.CenterV, 9);
    }

    [Fact]
    public void ScaledToPoint_OcclusionRight_TargetLandsAtVisibleZoneCenter()
    {
        // Dialog covers the right 40% (600..1000) → visible-zone center is 200px left
        // of the view center; the target must land there: target = center - dPx*scaleNew.
        var occlusion = new ScreenRect(600, 0, 1000, 500);

        var zoom = ViewZoomMath.ComputeScaledToPoint(-10, -5, 10, 5, 0.5, 7, 2, View, occlusion);

        Assert.NotNull(zoom);
        var scaleNew = zoom.HalfWidth * 2.0 / 1000.0;
        double visiblePointU = zoom.CenterU - 200.0 * scaleNew;
        Assert.Equal(7, visiblePointU, 9);
        Assert.Equal(5, zoom.HalfWidth, 9);   // 20 * 0.5 / 2
    }

    [Fact]
    public void ScaledToPoint_DegenerateInput_ReturnsNull()
    {
        Assert.Null(ViewZoomMath.ComputeScaledToPoint(0, 0, 0, 5, 0.5, 0, 0, View, null));  // zero width corners
        Assert.Null(ViewZoomMath.ComputeScaledToPoint(0, 0, 10, 5, 0, 0, 0, View, null));   // zero factor
        Assert.Null(ViewZoomMath.ComputeScaledToPoint(0, 0, 10, 5, 0.5, 0, 0, new ScreenRect(0, 0, 0, 0), null));
    }
}

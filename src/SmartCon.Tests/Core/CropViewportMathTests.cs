using SmartCon.Core.Services.FamilyManager;
using Xunit;

namespace SmartCon.Tests.Core;

/// <summary>
/// Tests for <see cref="CropViewportMath"/> (issue #131, ADR-047).
/// Reference geometry used across tests: viewport 600×450, frame 400×300 (4:3),
/// image 1600×1200 → fit = 0.375, minZoom = 2/3.
/// </summary>
public sealed class CropViewportMathTests
{
    private const double ViewW = 600;
    private const double ViewH = 450;
    private const double FrameW = 400;
    private const double FrameH = 300;
    private const double ImgW = 1600;
    private const double ImgH = 1200;
    private const double Fit = 0.375;       // min(600/1600, 450/1200)
    private const double MinZoom = 2.0 / 3; // max(400/600, 300/450)

    [Fact]
    public void FitScale_WideAndTallImages_PicksLimitingDimension()
    {
        Assert.Equal(0.375, CropViewportMath.FitScale(ViewW, ViewH, ImgW, ImgH), 6);
        Assert.Equal(0.1875, CropViewportMath.FitScale(ViewW, ViewH, 800, 2400), 6);
        // Very small image: fit > 1 (upscale to fill the viewport).
        Assert.Equal(6.0, CropViewportMath.FitScale(ViewW, ViewH, 100, 75), 6);
    }

    [Fact]
    public void MinZoom_FrameExactlyCovered()
    {
        Assert.Equal(MinZoom, CropViewportMath.MinZoom(FrameW, FrameH, ImgW, ImgH, Fit), 6);
    }

    [Fact]
    public void MinZoom_TinyImage_RequiresUpscaleBeyondFit()
    {
        // 100×100 image in 600×450 viewport: fit = 4.5 → displayed 450×450.
        // Frame 400×300 → minZoom = max(400/450, 300/450) = 8/9.
        var fit = CropViewportMath.FitScale(ViewW, ViewH, 100, 100);
        Assert.Equal(8.0 / 9, CropViewportMath.MinZoom(FrameW, FrameH, 100, 100, fit), 6);
    }

    [Fact]
    public void ClampOffset_ImageExactlyCoversFrame_ForcesZero()
    {
        // zoom = minZoom → displayed size == frame size → no pan freedom.
        var scale = Fit * MinZoom;
        var (x, y) = CropViewportMath.ClampOffset(
            500, -500, ViewW, ViewH, ImgW, ImgH, scale, 100, 75, FrameW, FrameH);
        Assert.Equal(0, x, 6);
        Assert.Equal(0, y, 6);
    }

    [Fact]
    public void ClampOffset_ZoomedIn_ClampsToCoverage()
    {
        // zoom = 2 → scale 0.75 → displayed 1200×900, base = ((600-1200)/2, (450-900)/2) = (-300, -225).
        // Frame at (100, 75): valid offsetX ∈ [100+400-1200+300, 100+300] = [-400, 400].
        var scale = Fit * 2;
        var (x, y) = CropViewportMath.ClampOffset(
            500, -1000, ViewW, ViewH, ImgW, ImgH, scale, 100, 75, FrameW, FrameH);
        Assert.Equal(400, x, 6);
        // offsetY ∈ [75+300-900+225, 75+225] = [-300, 300].
        Assert.Equal(-300, y, 6);
    }

    [Fact]
    public void ClampFrame_StaysInsideViewportAndImage()
    {
        var scale = Fit; // zoom = 1, offsets 0 → image covers the viewport exactly.
        var (x, y) = CropViewportMath.ClampFrame(
            -50, 500, FrameW, FrameH, ViewW, ViewH, ImgW, ImgH, scale, 0, 0, 0);
        Assert.Equal(0, x, 6);
        Assert.Equal(ViewH - FrameH, y, 6);
    }

    [Fact]
    public void ClampFrame_WithViewportInset_KeepsGutter()
    {
        var scale = Fit;
        var (x, y) = CropViewportMath.ClampFrame(
            -50, 500, FrameW, FrameH, ViewW, ViewH, ImgW, ImgH, scale, 0, 0, 10);
        Assert.Equal(10, x, 6);
        Assert.Equal(ViewH - 10 - FrameH, y, 6);
    }

    [Fact]
    public void ZoomAroundPoint_CenterAnchor_KeepsImageCentered()
    {
        var (zoom, offsetX, offsetY) = CropViewportMath.ZoomAroundPoint(
            1, 2, ViewW / 2, ViewH / 2,
            ViewW, ViewH, ImgW, ImgH, Fit, MinZoom, 8,
            0, 0, 100, 75, FrameW, FrameH);

        Assert.Equal(2, zoom, 6);
        Assert.Equal(0, offsetX, 6);
        Assert.Equal(0, offsetY, 6);
    }

    [Fact]
    public void ZoomAroundPoint_ClampsToMinZoom()
    {
        var (zoom, _, _) = CropViewportMath.ZoomAroundPoint(
            1, 0.01, 300, 225,
            ViewW, ViewH, ImgW, ImgH, Fit, MinZoom, 8,
            0, 0, 100, 75, FrameW, FrameH);

        Assert.Equal(MinZoom, zoom, 6);
    }

    [Fact]
    public void ZoomAroundPoint_ClampsToMaxZoom()
    {
        var (zoom, _, _) = CropViewportMath.ZoomAroundPoint(
            4, 100, 300, 225,
            ViewW, ViewH, ImgW, ImgH, Fit, MinZoom, 8,
            0, 0, 100, 75, FrameW, FrameH);

        Assert.Equal(8, zoom, 6);
    }

    [Fact]
    public void ToSourceRect_CenteredFitFrame_MapsToSourcePixels()
    {
        // zoom = 1 → scale 0.375, image top-left = (0, 0), frame at (100, 75).
        var rect = CropViewportMath.ToSourceRect(
            100, 75, FrameW, FrameH, ImgW, ImgH, Fit, 0, 0, ViewW, ViewH);

        Assert.Equal(100 / Fit, rect.X, 4);
        Assert.Equal(75 / Fit, rect.Y, 4);
        Assert.Equal(FrameW / Fit, rect.Width, 4);
        Assert.Equal(FrameH / Fit, rect.Height, 4);
        // Result keeps the 4:3 frame aspect.
        Assert.Equal(4.0 / 3, rect.Width / rect.Height, 4);
    }

    [Fact]
    public void ToSourceRect_MinZoomFullCoverage_ReturnsWholeImage()
    {
        // At minZoom the displayed image equals the frame → crop = whole image.
        var scale = Fit * MinZoom;
        var rect = CropViewportMath.ToSourceRect(
            100, 75, FrameW, FrameH, ImgW, ImgH, scale, 0, 0, ViewW, ViewH);

        Assert.Equal(0, rect.X, 4);
        Assert.Equal(0, rect.Y, 4);
        Assert.Equal(ImgW, rect.Width, 4);
        Assert.Equal(ImgH, rect.Height, 4);
    }

    [Fact]
    public void ToSourceRect_FrameOutsideImage_ClampsToBounds()
    {
        // Artificial inconsistency (frame beyond right edge) is clamped.
        var rect = CropViewportMath.ToSourceRect(
            500, 75, FrameW, FrameH, ImgW, ImgH, Fit, 0, 0, ViewW, ViewH);

        Assert.Equal(ImgW, rect.X + rect.Width, 4);
        Assert.True(rect.Width > 0);
    }

    // --- ResizeFrame (issue #131 rev 2: free aspect) ---
    // Geometry below: zoom = 1, offsets 0 → displayed image == viewport (600×450),
    // frame (100, 75, 400×300).

    [Fact]
    public void ResizeFrame_BottomRight_ResizesFreely()
    {
        var (x, y, w, h) = CropViewportMath.ResizeFrame(
            CropCorner.BottomRight, -100, -50,
            100, 75, FrameW, FrameH, 60, 60,
            ViewW, ViewH, ImgW, ImgH, Fit, 0, 0, 0);

        Assert.Equal(100, x, 6);
        Assert.Equal(75, y, 6);
        Assert.Equal(300, w, 6);
        Assert.Equal(250, h, 6);
    }

    [Fact]
    public void ResizeFrame_TopLeft_MovesOriginKeepsOppositeCorner()
    {
        var (x, y, w, h) = CropViewportMath.ResizeFrame(
            CropCorner.TopLeft, 50, 25,
            100, 75, FrameW, FrameH, 60, 60,
            ViewW, ViewH, ImgW, ImgH, Fit, 0, 0, 0);

        Assert.Equal(150, x, 6);
        Assert.Equal(100, y, 6);
        Assert.Equal(350, w, 6);
        Assert.Equal(275, h, 6);
    }

    [Fact]
    public void ResizeFrame_ClampsToMinSize()
    {
        var (_, _, w, h) = CropViewportMath.ResizeFrame(
            CropCorner.BottomRight, -10000, -10000,
            100, 75, FrameW, FrameH, 60, 60,
            ViewW, ViewH, ImgW, ImgH, Fit, 0, 0, 0);

        Assert.Equal(60, w, 6);
        Assert.Equal(60, h, 6);
    }

    [Fact]
    public void ResizeFrame_ClampsToViewportAndImage()
    {
        var (x, y, w, h) = CropViewportMath.ResizeFrame(
            CropCorner.BottomRight, 10000, 10000,
            100, 75, FrameW, FrameH, 60, 60,
            ViewW, ViewH, ImgW, ImgH, Fit, 0, 0, 0);

        // Image == viewport here: bounds are (0,0)-(600,450).
        Assert.Equal(100, x, 6);
        Assert.Equal(75, y, 6);
        Assert.Equal(500, w, 6);
        Assert.Equal(375, h, 6);
    }

    [Fact]
    public void ResizeFrame_TopLeft_ClampsToBounds()
    {
        var (x, y, w, h) = CropViewportMath.ResizeFrame(
            CropCorner.TopLeft, -10000, -10000,
            100, 75, FrameW, FrameH, 60, 60,
            ViewW, ViewH, ImgW, ImgH, Fit, 0, 0, 0);

        Assert.Equal(0, x, 6);
        Assert.Equal(0, y, 6);
        Assert.Equal(500, w, 6);
        Assert.Equal(375, h, 6);
    }

    // --- Viewport inset gutter (issue #131 rev 4: handles stay grabbable) ---

    [Fact]
    public void ResizeFrame_WithViewportInset_KeepsGutter()
    {
        var (x, y, w, h) = CropViewportMath.ResizeFrame(
            CropCorner.BottomRight, 10000, 10000,
            100, 75, FrameW, FrameH, 60, 60,
            ViewW, ViewH, ImgW, ImgH, Fit, 0, 0, 10);

        Assert.Equal(100, x, 6);
        Assert.Equal(75, y, 6);
        Assert.Equal(ViewW - 10 - 100, w, 6); // right bound = 590
        Assert.Equal(ViewH - 10 - 75, h, 6);  // bottom bound = 440
    }

    [Fact]
    public void ResizeFrame_TopLeft_WithViewportInset_KeepsGutter()
    {
        var (x, y, _, _) = CropViewportMath.ResizeFrame(
            CropCorner.TopLeft, -10000, -10000,
            100, 75, FrameW, FrameH, 60, 60,
            ViewW, ViewH, ImgW, ImgH, Fit, 0, 0, 10);

        Assert.Equal(10, x, 6);
        Assert.Equal(10, y, 6);
    }
}

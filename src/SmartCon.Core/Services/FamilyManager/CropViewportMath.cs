using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.FamilyManager;

/// <summary>Corner of the crop frame grabbed for a resize drag (issue #131 rev 2).</summary>
public enum CropCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>
/// Pure math for the avatar crop dialog viewport (issue #131, ADR-047).
/// Coordinate space: viewport units, origin top-left. The image is displayed at
/// <c>scale = fitScale * zoom</c> where zoom=1 means "whole image fits the viewport",
/// positioned centered plus a pan offset. A fixed-size 4:3 crop frame moves over the
/// viewport. Invariant: the displayed image always fully covers the crop frame,
/// and the frame stays inside the viewport.
/// </summary>
public static class CropViewportMath
{
    /// <summary>Scale at which the whole image fits inside the viewport (zoom = 1).</summary>
    public static double FitScale(double viewWidth, double viewHeight, double imageWidth, double imageHeight)
        => System.Math.Min(viewWidth / imageWidth, viewHeight / imageHeight);

    /// <summary>
    /// Minimum zoom at which the displayed image can still cover the frame
    /// (displayed width/height must be at least the frame width/height).
    /// </summary>
    public static double MinZoom(double frameWidth, double frameHeight, double imageWidth, double imageHeight, double fitScale)
        => System.Math.Max(frameWidth / (imageWidth * fitScale), frameHeight / (imageHeight * fitScale));

    /// <summary>Top-left corner of the displayed image in viewport coordinates.</summary>
    public static (double Left, double Top) GetImageTopLeft(
        double viewWidth, double viewHeight, double imageWidth, double imageHeight,
        double scale, double offsetX, double offsetY)
    {
        var displayWidth = imageWidth * scale;
        var displayHeight = imageHeight * scale;
        return ((viewWidth - displayWidth) / 2 + offsetX, (viewHeight - displayHeight) / 2 + offsetY);
    }

    /// <summary>
    /// Clamp the pan offset so the displayed image keeps covering the frame.
    /// Caller must guarantee zoom >= MinZoom (displayed size >= frame size).
    /// </summary>
    public static (double X, double Y) ClampOffset(
        double offsetX, double offsetY,
        double viewWidth, double viewHeight, double imageWidth, double imageHeight,
        double scale, double frameX, double frameY, double frameWidth, double frameHeight)
    {
        var displayWidth = imageWidth * scale;
        var displayHeight = imageHeight * scale;
        var baseX = (viewWidth - displayWidth) / 2;
        var baseY = (viewHeight - displayHeight) / 2;

        // left = baseX + offsetX must stay within [frameX + frameWidth - displayWidth, frameX]
        var minOffsetX = frameX + frameWidth - displayWidth - baseX;
        var maxOffsetX = frameX - baseX;
        var minOffsetY = frameY + frameHeight - displayHeight - baseY;
        var maxOffsetY = frameY - baseY;

        return (Clamp(offsetX, minOffsetX, maxOffsetX), Clamp(offsetY, minOffsetY, maxOffsetY));
    }

    /// <summary>
    /// Clamp the frame position so it stays inside the displayed image and inside the
    /// viewport inset gutter (<paramref name="viewportInset"/>). The gutter (Gutenberg
    /// PR #77547 pattern) keeps the protruding corner handles fully visible — they
    /// would otherwise slide under the viewport's ClipToBounds at maximum expansion.
    /// </summary>
    public static (double X, double Y) ClampFrame(
        double frameX, double frameY, double frameWidth, double frameHeight,
        double viewWidth, double viewHeight, double imageWidth, double imageHeight,
        double scale, double offsetX, double offsetY, double viewportInset)
    {
        var (left, top) = GetImageTopLeft(viewWidth, viewHeight, imageWidth, imageHeight, scale, offsetX, offsetY);
        var displayWidth = imageWidth * scale;
        var displayHeight = imageHeight * scale;

        var minX = System.Math.Max(left, viewportInset);
        var maxX = System.Math.Min(left + displayWidth - frameWidth, viewWidth - viewportInset - frameWidth);
        var minY = System.Math.Max(top, viewportInset);
        var maxY = System.Math.Min(top + displayHeight - frameHeight, viewHeight - viewportInset - frameHeight);

        return (Clamp(frameX, minX, maxX), Clamp(frameY, minY, maxY));
    }

    /// <summary>
    /// Compute the new (zoom, offsetX, offsetY) when zooming by <paramref name="factor"/>
    /// anchored at viewport point (<paramref name="viewX"/>, <paramref name="viewY"/>):
    /// the image point under the anchor stays fixed. Result is clamped to
    /// [minZoom, maxZoom] and to the frame-coverage invariant.
    /// </summary>
    public static (double Zoom, double OffsetX, double OffsetY) ZoomAroundPoint(
        double zoom, double factor, double viewX, double viewY,
        double viewWidth, double viewHeight, double imageWidth, double imageHeight,
        double fitScale, double minZoom, double maxZoom,
        double offsetX, double offsetY,
        double frameX, double frameY, double frameWidth, double frameHeight)
    {
        var newZoom = Clamp(zoom * factor, minZoom, maxZoom);
        var oldScale = fitScale * zoom;
        var newScale = fitScale * newZoom;

        var (left, top) = GetImageTopLeft(viewWidth, viewHeight, imageWidth, imageHeight, oldScale, offsetX, offsetY);
        var newLeft = viewX - (viewX - left) * (newScale / oldScale);
        var newTop = viewY - (viewY - top) * (newScale / oldScale);

        var displayWidth = imageWidth * newScale;
        var displayHeight = imageHeight * newScale;
        var newOffsetX = newLeft - (viewWidth - displayWidth) / 2;
        var newOffsetY = newTop - (viewHeight - displayHeight) / 2;

        (newOffsetX, newOffsetY) = ClampOffset(
            newOffsetX, newOffsetY, viewWidth, viewHeight, imageWidth, imageHeight,
            newScale, frameX, frameY, frameWidth, frameHeight);

        return (newZoom, newOffsetX, newOffsetY);
    }

    /// <summary>
    /// Resize the crop frame by dragging one of its corners (free aspect, issue #131 rev 2).
    /// The opposite corner stays pinned; the frame is clamped to the min size and to
    /// the intersection of the displayed image and the viewport.
    /// </summary>
    public static (double X, double Y, double Width, double Height) ResizeFrame(
        CropCorner corner, double dx, double dy,
        double frameX, double frameY, double frameWidth, double frameHeight,
        double minWidth, double minHeight,
        double viewWidth, double viewHeight, double imageWidth, double imageHeight,
        double scale, double offsetX, double offsetY, double viewportInset)
    {
        var (imgLeft, imgTop) = GetImageTopLeft(viewWidth, viewHeight, imageWidth, imageHeight, scale, offsetX, offsetY);
        var imgRight = imgLeft + imageWidth * scale;
        var imgBottom = imgTop + imageHeight * scale;

        var left = frameX;
        var top = frameY;
        var right = frameX + frameWidth;
        var bottom = frameY + frameHeight;

        switch (corner)
        {
            case CropCorner.TopLeft:
                left += dx;
                top += dy;
                break;
            case CropCorner.TopRight:
                right += dx;
                top += dy;
                break;
            case CropCorner.BottomLeft:
                left += dx;
                bottom += dy;
                break;
            case CropCorner.BottomRight:
                right += dx;
                bottom += dy;
                break;
        }

        // Gutter-inset bounds: same rationale as ClampFrame (handles stay grabbable).
        var boundLeft = System.Math.Max(imgLeft, viewportInset);
        var boundTop = System.Math.Max(imgTop, viewportInset);
        var boundRight = System.Math.Min(imgRight, viewWidth - viewportInset);
        var boundBottom = System.Math.Min(imgBottom, viewHeight - viewportInset);

        if (corner is CropCorner.TopLeft or CropCorner.BottomLeft)
            left = Clamp(left, boundLeft, right - minWidth);
        if (corner is CropCorner.TopLeft or CropCorner.TopRight)
            top = Clamp(top, boundTop, bottom - minHeight);
        if (corner is CropCorner.TopRight or CropCorner.BottomRight)
            right = Clamp(right, left + minWidth, boundRight);
        if (corner is CropCorner.BottomLeft or CropCorner.BottomRight)
            bottom = Clamp(bottom, top + minHeight, boundBottom);

        return (left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Map the current frame position to a crop rectangle in source-image pixels.
    /// Result is clamped to the image bounds.
    /// </summary>
    public static ImageCropRect ToSourceRect(
        double frameX, double frameY, double frameWidth, double frameHeight,
        double imageWidth, double imageHeight, double scale,
        double offsetX, double offsetY, double viewWidth, double viewHeight)
    {
        var (left, top) = GetImageTopLeft(viewWidth, viewHeight, imageWidth, imageHeight, scale, offsetX, offsetY);

        var x = Clamp((frameX - left) / scale, 0, imageWidth);
        var y = Clamp((frameY - top) / scale, 0, imageHeight);
        var w = System.Math.Min(frameWidth / scale, imageWidth - x);
        var h = System.Math.Min(frameHeight / scale, imageHeight - y);

        return new ImageCropRect(x, y, w, h);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (min > max)
            (min, max) = (max, min);
        return value < min ? min : value > max ? max : value;
    }
}

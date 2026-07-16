using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Common;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// ViewModel for the avatar crop dialog (issue #131, ADR-047).
/// WPF-free (doubles only) so it is unit-testable: the view binds
/// Image.Left/Top/Width/Height and the frame Thumb to these properties, while
/// pan/zoom/frame mouse input arrives through behaviors calling
/// <see cref="PanBy"/>, <see cref="ZoomAt(double, double, double)"/> and
/// <see cref="MoveFrame"/>. Invariant (enforced by CropViewportMath): the
/// displayed image always fully covers the crop frame.
/// </summary>
public sealed partial class CropAvatarViewModel : ObservableObject, IObservableRequestClose
{
    /// <summary>Viewport width in WPF units (matches CropAvatarView).</summary>
    public const double ViewportWidth = 600;

    /// <summary>Viewport height in WPF units (matches CropAvatarView).</summary>
    public const double ViewportHeight = 450;

    /// <summary>Default crop frame width in WPF units (4:3). The frame is freely resizable
    /// via corner handles; the output is always rendered to 560×420 with cover semantics
    /// (overflow edges are cropped) — see ADR-047 rev 2.</summary>
    public const double DefaultFrameWidth = 400;

    /// <summary>Default crop frame height in WPF units (4:3).</summary>
    public const double DefaultFrameHeight = 300;

    /// <summary>Minimum crop frame size in WPF units (free aspect resize clamp).</summary>
    public const double MinFrameSize = 60;

    /// <summary>
    /// Gutter between the crop frame and the viewport edges (issue #131 rev 4):
    /// keeps the protruding corner handles (7px) fully visible at maximum frame
    /// expansion instead of sliding under the viewport's ClipToBounds.
    /// </summary>
    public const double FrameViewportInset = 10;

    private const double MaxZoomFactor = 8.0;

    private readonly IAvatarCropService _cropService;
    private readonly double _fitScale;
    private double _lastAppliedZoom;

    [ObservableProperty] private string _imagePath;
    [ObservableProperty] private double _zoom;
    [ObservableProperty] private double _minZoom;
    [ObservableProperty] private double _maxZoom;
    [ObservableProperty] private double _offsetX;
    [ObservableProperty] private double _offsetY;
    [ObservableProperty] private double _frameX;
    [ObservableProperty] private double _frameY;
    [ObservableProperty] private double _frameWidth = DefaultFrameWidth;
    [ObservableProperty] private double _frameHeight = DefaultFrameHeight;
    [ObservableProperty] private double _imageDisplayWidth;
    [ObservableProperty] private double _imageDisplayHeight;
    [ObservableProperty] private double _imageLeft;
    [ObservableProperty] private double _imageTop;

    /// <summary>Preview box width in WPF units (matches CropAvatarView preview panel).</summary>
    public const double PreviewBoxWidth = 140;

    /// <summary>Preview box height in WPF units (matches CropAvatarView preview panel).</summary>
    public const double PreviewBoxHeight = 105;

    /// <summary>Live-preview selection rect in NATIVE source pixels (rev 6) — consumed
    /// by PreviewCropImageBehavior to produce a CroppedBitmap identical to the
    /// renderer's output.</summary>
    [ObservableProperty] private double _previewRectX;
    [ObservableProperty] private double _previewRectY;
    [ObservableProperty] private double _previewRectWidth;
    [ObservableProperty] private double _previewRectHeight;

    /// <summary>Live-preview display geometry (rev 6): where the cropped selection image
    /// sits inside the 140×105 preview box — contain-fitted and centered.</summary>
    [ObservableProperty] private double _previewViewLeft;
    [ObservableProperty] private double _previewViewTop;
    [ObservableProperty] private double _previewViewWidth;
    [ObservableProperty] private double _previewViewHeight;

    /// <summary>Path of the rendered 560×420 PNG in the temp folder. Set on Apply.</summary>
    [ObservableProperty] private string? _resultPath;

    /// <summary>Error message when Apply failed to render the crop; null otherwise.</summary>
    [ObservableProperty] private string? _applyError;

    public int SourcePixelWidth { get; }
    public int SourcePixelHeight { get; }

    public string ZoomText => (_lastAppliedZoom * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    public event Action<bool?>? RequestClose;

    public CropAvatarViewModel(string imagePath, int pixelWidth, int pixelHeight, IAvatarCropService cropService)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentNullException(nameof(imagePath));
        _cropService = cropService ?? throw new ArgumentNullException(nameof(cropService));
        Guard.ThrowIfNegativeOrZero(pixelWidth);
        Guard.ThrowIfNegativeOrZero(pixelHeight);

        _imagePath = imagePath;
        SourcePixelWidth = pixelWidth;
        SourcePixelHeight = pixelHeight;

        _fitScale = CropViewportMath.FitScale(ViewportWidth, ViewportHeight, pixelWidth, pixelHeight);
        _minZoom = CropViewportMath.MinZoom(FrameWidth, FrameHeight, pixelWidth, pixelHeight, _fitScale);
        _maxZoom = Math.Max(_minZoom, MaxZoomFactor);

        // Start: frame centered, image centered, frame exactly covered (most zoomed-out allowed).
        _frameX = (ViewportWidth - DefaultFrameWidth) / 2;
        _frameY = (ViewportHeight - DefaultFrameHeight) / 2;
        _zoom = _minZoom;
        _lastAppliedZoom = _minZoom;
        RecalculateLayout();
    }

    /// <summary>Pan the image by a viewport-space delta (called from the view behavior).</summary>
    public void PanBy(double dx, double dy)
    {
        var scale = _fitScale * _lastAppliedZoom;
        (OffsetX, OffsetY) = CropViewportMath.ClampOffset(
            OffsetX + dx, OffsetY + dy,
            ViewportWidth, ViewportHeight, SourcePixelWidth, SourcePixelHeight,
            scale, FrameX, FrameY, FrameWidth, FrameHeight);
        RecalculateLayout();
    }

    /// <summary>Zoom by a factor anchored at a viewport point (mouse wheel).</summary>
    public void ZoomAt(double factor, double viewX, double viewY)
    {
        var (zoom, offsetX, offsetY) = CropViewportMath.ZoomAroundPoint(
            _lastAppliedZoom, factor, viewX, viewY,
            ViewportWidth, ViewportHeight, SourcePixelWidth, SourcePixelHeight,
            _fitScale, MinZoom, MaxZoom,
            OffsetX, OffsetY, FrameX, FrameY, FrameWidth, FrameHeight);
        _lastAppliedZoom = zoom;
        Zoom = zoom;
        OffsetX = offsetX;
        OffsetY = offsetY;
        RecalculateLayout();
    }

    /// <summary>Move the crop frame by a viewport-space delta (Thumb drag).</summary>
    public void MoveFrame(double dx, double dy)
    {
        var scale = _fitScale * _lastAppliedZoom;
        (FrameX, FrameY) = CropViewportMath.ClampFrame(
            FrameX + dx, FrameY + dy, FrameWidth, FrameHeight,
            ViewportWidth, ViewportHeight, SourcePixelWidth, SourcePixelHeight,
            scale, OffsetX, OffsetY, FrameViewportInset);
        RecalculateLayout();
    }

    /// <summary>
    /// Resize the crop frame by dragging a corner handle (free aspect, issue #131 rev 2).
    /// After the resize the minimum zoom is recomputed (a larger frame may force
    /// a zoom-in so the image still covers the frame).
    /// </summary>
    public void ResizeFrame(CropCorner corner, double dx, double dy)
    {
        var scale = _fitScale * _lastAppliedZoom;
        (FrameX, FrameY, FrameWidth, FrameHeight) = CropViewportMath.ResizeFrame(
            corner, dx, dy, FrameX, FrameY, FrameWidth, FrameHeight,
            MinFrameSize, MinFrameSize,
            ViewportWidth, ViewportHeight, SourcePixelWidth, SourcePixelHeight,
            scale, OffsetX, OffsetY, FrameViewportInset);

        MinZoom = CropViewportMath.MinZoom(FrameWidth, FrameHeight, SourcePixelWidth, SourcePixelHeight, _fitScale);
        MaxZoom = Math.Max(MinZoom, MaxZoomFactor);
        if (_lastAppliedZoom < MinZoom)
        {
            _lastAppliedZoom = MinZoom;
            Zoom = MinZoom;
        }

        var newScale = _fitScale * _lastAppliedZoom;
        (OffsetX, OffsetY) = CropViewportMath.ClampOffset(
            OffsetX, OffsetY, ViewportWidth, ViewportHeight, SourcePixelWidth, SourcePixelHeight,
            newScale, FrameX, FrameY, FrameWidth, FrameHeight);
        RecalculateLayout();
    }

    partial void OnZoomChanged(double value)
    {
        if (Math.Abs(value - _lastAppliedZoom) < 1e-9)
        {
            RecalculateLayout();
            return;
        }

        // External change (zoom slider): re-clamp anchored at the frame center.
        // The backing field _zoom already holds the new value, so compute the
        // factor against the last applied zoom and write the clamped result back.
        var old = _lastAppliedZoom;
        var (zoom, offsetX, offsetY) = CropViewportMath.ZoomAroundPoint(
            old, value / old, FrameX + FrameWidth / 2, FrameY + FrameHeight / 2,
            ViewportWidth, ViewportHeight, SourcePixelWidth, SourcePixelHeight,
            _fitScale, MinZoom, MaxZoom,
            OffsetX, OffsetY, FrameX, FrameY, FrameWidth, FrameHeight);
        _lastAppliedZoom = zoom;
        _zoom = zoom;
        OffsetX = offsetX;
        OffsetY = offsetY;
        RecalculateLayout();
    }

    [RelayCommand]
    private void Reset()
    {
        FrameWidth = DefaultFrameWidth;
        FrameHeight = DefaultFrameHeight;
        MinZoom = CropViewportMath.MinZoom(FrameWidth, FrameHeight, SourcePixelWidth, SourcePixelHeight, _fitScale);
        MaxZoom = Math.Max(MinZoom, MaxZoomFactor);
        _lastAppliedZoom = MinZoom;
        Zoom = MinZoom;
        OffsetX = 0;
        OffsetY = 0;
        FrameX = (ViewportWidth - DefaultFrameWidth) / 2;
        FrameY = (ViewportHeight - DefaultFrameHeight) / 2;
        RecalculateLayout();
    }

    [RelayCommand]
    private void Apply()
    {
        using var _scope = SmartConLogger.BeginScope("FMCrop",
            ("Method", nameof(Apply)),
            ("FileName", Path.GetFileName(ImagePath)));

        var scale = _fitScale * _lastAppliedZoom;
        var rect = CropViewportMath.ToSourceRect(
            FrameX, FrameY, FrameWidth, FrameHeight,
            SourcePixelWidth, SourcePixelHeight, scale,
            OffsetX, OffsetY, ViewportWidth, ViewportHeight);

        var tempPath = Path.Combine(Path.GetTempPath(), "smartcon-avatar-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            _cropService.CropToPng(ImagePath, rect, tempPath);
            ResultPath = tempPath;
            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"Apply: failed to crop '{Path.GetFileName(ImagePath)}': {ex.Message} [Action: проверьте, что файл изображения не повреждён и доступен для чтения]");
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            ApplyError = ex.Message;
            RequestClose?.Invoke(null);
        }
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(null);

    private void RecalculateLayout()
    {
        var scale = _fitScale * _lastAppliedZoom;
        ImageDisplayWidth = SourcePixelWidth * scale;
        ImageDisplayHeight = SourcePixelHeight * scale;
        (ImageLeft, ImageTop) = CropViewportMath.GetImageTopLeft(
            ViewportWidth, ViewportHeight, SourcePixelWidth, SourcePixelHeight, scale, OffsetX, OffsetY);

        var rect = CropViewportMath.ToSourceRect(
            FrameX, FrameY, FrameWidth, FrameHeight,
            SourcePixelWidth, SourcePixelHeight, scale,
            OffsetX, OffsetY, ViewportWidth, ViewportHeight);

        // Live preview (rev 6): selection rect in source pixels (PreviewRect*, cropped
        // by PreviewCropImageBehavior at the source level) + display geometry of the
        // cropped result in the preview box (PreviewView*, contain-fitted, centered) —
        // pixel-identical to the renderer's crop+contain output.
        var previewFit = System.Math.Min(
            PreviewBoxWidth / rect.Width,
            PreviewBoxHeight / rect.Height);
        PreviewRectX = rect.X;
        PreviewRectY = rect.Y;
        PreviewRectWidth = rect.Width;
        PreviewRectHeight = rect.Height;
        PreviewViewWidth = rect.Width * previewFit;
        PreviewViewHeight = rect.Height * previewFit;
        PreviewViewLeft = (PreviewBoxWidth - PreviewViewWidth) / 2;
        PreviewViewTop = (PreviewBoxHeight - PreviewViewHeight) / 2;

        OnPropertyChanged(nameof(ZoomText));
    }
}

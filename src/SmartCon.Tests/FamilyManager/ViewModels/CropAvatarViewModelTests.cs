using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Tests for <see cref="CropAvatarViewModel"/> (issue #131, ADR-047).
/// Reference geometry: viewport 600×450, frame 400×300, image 1600×1200
/// → fit = 0.375, minZoom = 2/3, initial display = 400×300 (frame exactly covered).
/// </summary>
public sealed class CropAvatarViewModelTests
{
    private const string ImagePath = @"C:\images\source.png";

    private static (CropAvatarViewModel vm, Mock<IAvatarCropService> cropService) MakeVm(
        int pixelWidth = 1600, int pixelHeight = 1200)
    {
        var cropService = new Mock<IAvatarCropService>();
        var vm = new CropAvatarViewModel(ImagePath, pixelWidth, pixelHeight, cropService.Object);
        return (vm, cropService);
    }

    [Fact]
    public void Ctor_InitialState_FrameCenteredAndExactlyCovered()
    {
        var (vm, _) = MakeVm();

        Assert.Equal(2.0 / 3, vm.MinZoom, 6);
        Assert.Equal(vm.MinZoom, vm.Zoom, 6);
        Assert.Equal(8.0, vm.MaxZoom, 6);
        Assert.Equal(100, vm.FrameX);
        Assert.Equal(75, vm.FrameY);
        Assert.Equal(0, vm.OffsetX);
        Assert.Equal(0, vm.OffsetY);
        // Displayed image exactly equals the frame and is centered over it.
        Assert.Equal(400, vm.ImageDisplayWidth, 6);
        Assert.Equal(300, vm.ImageDisplayHeight, 6);
        Assert.Equal(100, vm.ImageLeft, 6);
        Assert.Equal(75, vm.ImageTop, 6);
        Assert.Null(vm.ResultPath);
        Assert.Null(vm.ApplyError);
    }

    [Fact]
    public void Ctor_TinyImage_UpscalesToCoverFrame()
    {
        var (vm, _) = MakeVm(100, 75);

        // fit = 6, minZoom = 2/3 → display = 100*6*(2/3) = 400×300.
        Assert.Equal(400, vm.ImageDisplayWidth, 6);
        Assert.Equal(300, vm.ImageDisplayHeight, 6);
    }

    [Fact]
    public void Ctor_InvalidArguments_Throws()
    {
        var cropService = new Mock<IAvatarCropService>().Object;
        Assert.Throws<ArgumentNullException>(() => new CropAvatarViewModel(null!, 100, 100, cropService));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CropAvatarViewModel(ImagePath, 0, 100, cropService));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CropAvatarViewModel(ImagePath, 100, -5, cropService));
        Assert.Throws<ArgumentNullException>(() => new CropAvatarViewModel(ImagePath, 100, 100, null!));
    }

    [Fact]
    public void PanBy_AtMinZoom_NoFreedom_OffsetsStayZero()
    {
        var (vm, _) = MakeVm();

        vm.PanBy(500, -500);

        Assert.Equal(0, vm.OffsetX);
        Assert.Equal(0, vm.OffsetY);
    }

    [Fact]
    public void PanBy_ZoomedIn_PansAndClamps()
    {
        var (vm, _) = MakeVm();
        vm.ZoomAt(2, 300, 225); // zoom = 4/3 → display 800×600

        vm.PanBy(50, 25);
        Assert.Equal(50, vm.OffsetX, 6);
        Assert.Equal(25, vm.OffsetY, 6);

        // Huge pan gets clamped to keep the image covering the frame.
        vm.PanBy(10000, 10000);
        var (expectedX, expectedY) = SmartCon.Core.Services.FamilyManager.CropViewportMath.ClampOffset(
            10050, 10025, 600, 450, 1600, 1200, 0.375 * (4.0 / 3), 100, 75, 400, 300);
        Assert.Equal(expectedX, vm.OffsetX, 6);
        Assert.Equal(expectedY, vm.OffsetY, 6);
    }

    [Fact]
    public void ZoomAt_ClampsToMinAndMax()
    {
        var (vm, _) = MakeVm();

        vm.ZoomAt(1000, 300, 225);
        Assert.Equal(8.0, vm.Zoom, 6);

        vm.ZoomAt(0.0001, 300, 225);
        Assert.Equal(vm.MinZoom, vm.Zoom, 6);
    }

    [Fact]
    public void Zoom_PropertySetBeyondMax_SliderPath_ClampsBack()
    {
        var (vm, _) = MakeVm();

        // Simulates the TwoWay slider writing an out-of-range value.
        vm.Zoom = 100;

        Assert.Equal(8.0, vm.Zoom, 6);
    }

    [Fact]
    public void MoveFrame_ClampsInsideViewportAndImage()
    {
        var (vm, _) = MakeVm();

        vm.MoveFrame(-500, 0);
        Assert.Equal(100, vm.FrameX, 6); // at minZoom image == frame: no freedom

        vm.ZoomAt(2, 300, 225); // display 800×600
        vm.MoveFrame(1000, 1000);
        Assert.Equal(190, vm.FrameX, 6); // viewport right edge with 10px gutter: 600-10-400
        Assert.Equal(140, vm.FrameY, 6); // viewport bottom edge with 10px gutter: 450-10-300
    }

    [Fact]
    public void Reset_RestoresInitialState()
    {
        var (vm, _) = MakeVm();
        vm.ZoomAt(2, 300, 225);
        vm.PanBy(50, 50);
        vm.MoveFrame(20, 20);

        vm.ResetCommand.Execute(null);

        Assert.Equal(vm.MinZoom, vm.Zoom, 6);
        Assert.Equal(0, vm.OffsetX);
        Assert.Equal(0, vm.OffsetY);
        Assert.Equal(100, vm.FrameX);
        Assert.Equal(75, vm.FrameY);
    }

    [Fact]
    public void Apply_CallsCropServiceWithSourceRect_AndClosesWithResult()
    {
        var (vm, cropService) = MakeVm();
        bool? closedWith = null;
        vm.RequestClose += r => closedWith = r;

        vm.ApplyCommand.Execute(null);

        cropService.Verify(x => x.CropToPng(
            ImagePath,
            It.Is<ImageCropRect>(r =>
                System.Math.Abs(r.X) < 0.01 &&
                System.Math.Abs(r.Y) < 0.01 &&
                System.Math.Abs(r.Width - 1600) < 0.01 &&
                System.Math.Abs(r.Height - 1200) < 0.01),
            It.Is<string>(p => p.EndsWith(".png"))), Times.Once);
        Assert.NotNull(vm.ResultPath);
        Assert.True(closedWith);
        Assert.Null(vm.ApplyError);
    }

    [Fact]
    public void Apply_CropServiceThrows_SetsApplyError_AndClosesWithNull()
    {
        var (vm, cropService) = MakeVm();
        cropService
            .Setup(x => x.CropToPng(It.IsAny<string>(), It.IsAny<ImageCropRect>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("decode failed"));
        bool? closedWith = true;
        vm.RequestClose += r => closedWith = r;

        vm.ApplyCommand.Execute(null);

        Assert.Null(closedWith);
        Assert.Null(vm.ResultPath);
        Assert.Equal("decode failed", vm.ApplyError);
    }

    [Fact]
    public void Cancel_ClosesWithNull()
    {
        var (vm, _) = MakeVm();
        bool? closedWith = true;
        vm.RequestClose += r => closedWith = r;

        vm.CancelCommand.Execute(null);

        Assert.Null(closedWith);
        Assert.Null(vm.ResultPath);
    }

    [Fact]
    public void ZoomText_ReflectsCurrentZoom()
    {
        var (vm, _) = MakeVm();
        Assert.Equal("67%", vm.ZoomText);

        vm.ZoomAt(3, 300, 225); // 2 (clamped from 2/3*3 = 2)
        Assert.Equal("200%", vm.ZoomText);
    }

    // --- ResizeFrame (issue #131 rev 2: free aspect) ---

    [Fact]
    public void ResizeFrame_BottomRight_FreeAspectResize()
    {
        var (vm, _) = MakeVm();

        vm.ResizeFrame(CropCorner.BottomRight, -100, -50);

        Assert.Equal(100, vm.FrameX, 6);
        Assert.Equal(75, vm.FrameY, 6);
        Assert.Equal(300, vm.FrameWidth, 6);
        Assert.Equal(250, vm.FrameHeight, 6);
    }

    [Fact]
    public void ResizeFrame_ClampsToMinSize()
    {
        var (vm, _) = MakeVm();

        vm.ResizeFrame(CropCorner.BottomRight, -10000, -10000);

        Assert.Equal(CropAvatarViewModel.MinFrameSize, vm.FrameWidth, 6);
        Assert.Equal(CropAvatarViewModel.MinFrameSize, vm.FrameHeight, 6);
    }

    [Fact]
    public void ResizeFrame_TopLeft_MovesOrigin()
    {
        var (vm, _) = MakeVm();

        vm.ResizeFrame(CropCorner.TopLeft, 50, 25);

        Assert.Equal(150, vm.FrameX, 6);
        Assert.Equal(100, vm.FrameY, 6);
        Assert.Equal(350, vm.FrameWidth, 6);
        Assert.Equal(275, vm.FrameHeight, 6);
    }

    [Fact]
    public void ResizeFrame_RecomputesMinZoom()
    {
        var (vm, _) = MakeVm();
        vm.ZoomAt(3, 300, 225); // zoom = 2 → display 1200×900, resize freedom

        vm.ResizeFrame(CropCorner.BottomRight, 50, 40);

        var expected = SmartCon.Core.Services.FamilyManager.CropViewportMath.MinZoom(
            vm.FrameWidth, vm.FrameHeight, 1600, 1200, 0.375);
        Assert.Equal(expected, vm.MinZoom, 6);
        Assert.True(vm.Zoom >= vm.MinZoom);
    }

    [Fact]
    public void Reset_RestoresDefaultFrameSize()
    {
        var (vm, _) = MakeVm();
        vm.ResizeFrame(CropCorner.BottomRight, -200, -150);

        vm.ResetCommand.Execute(null);

        Assert.Equal(CropAvatarViewModel.DefaultFrameWidth, vm.FrameWidth, 6);
        Assert.Equal(CropAvatarViewModel.DefaultFrameHeight, vm.FrameHeight, 6);
        Assert.Equal(100, vm.FrameX);
        Assert.Equal(75, vm.FrameY);
    }

    [Fact]
    public void PreviewTransform_MirrorsContainRenderer()
    {
        var (vm, _) = MakeVm();

        // Initial: selection = whole 1600×1200 image.
        // contain fit into 140×105: fit = min(140/1600, 105/1200) = 0.0875.
        Assert.Equal(0, vm.PreviewRectX, 4);
        Assert.Equal(0, vm.PreviewRectY, 4);
        Assert.Equal(1600, vm.PreviewRectWidth, 4);
        Assert.Equal(1200, vm.PreviewRectHeight, 4);
        Assert.Equal(140, vm.PreviewViewWidth, 4);
        Assert.Equal(105, vm.PreviewViewHeight, 4);
        Assert.Equal(0, vm.PreviewViewLeft, 4);
        Assert.Equal(0, vm.PreviewViewTop, 4);

        // Frame 200×150 at the same top-left (scale 0.25 → rect 800×600 px at (0,0)).
        vm.ResizeFrame(CropCorner.BottomRight, -200, -150);
        // contain fit: min(140/800, 105/600) = 0.175.
        Assert.Equal(800, vm.PreviewRectWidth, 4);
        Assert.Equal(600, vm.PreviewRectHeight, 4);
        Assert.Equal(140, vm.PreviewViewWidth, 4);
        Assert.Equal(105, vm.PreviewViewHeight, 4);
    }

    [Fact]
    public void PreviewTransform_PannedSelection_RectMatchesSourcePixels()
    {
        var (vm, _) = MakeVm();
        vm.ZoomAt(3, 300, 225); // zoom = 2, display 1200×900, left = -300, top = -225
        vm.PanBy(50, 25);

        // scale = 0.75; image top-left = (-250, -200).
        // rect = (466.67, 366.67, 533.33, 400) px — exactly 4:3.
        Assert.Equal(350.0 / 0.75, vm.PreviewRectX, 3);
        Assert.Equal(275.0 / 0.75, vm.PreviewRectY, 3);
        Assert.Equal(400.0 / 0.75, vm.PreviewRectWidth, 3);
        Assert.Equal(300.0 / 0.75, vm.PreviewRectHeight, 3);
        // contain fit: min(140/533.33, 105/400) = 0.2625 → selection fills the box.
        Assert.Equal(140, vm.PreviewViewWidth, 3);
        Assert.Equal(105, vm.PreviewViewHeight, 3);
        Assert.Equal(0, vm.PreviewViewLeft, 3);
        Assert.Equal(0, vm.PreviewViewTop, 3);
    }

    [Fact]
    public void PreviewTransform_TallSelection_SymmetricSideMargins()
    {
        var (vm, _) = MakeVm(800, 1200); // portrait source
        // Narrow the frame to 200×300 → rect 400×600 px (tall), contain fit:
        // min(140/400, 105/600) = 0.175 → shown 70×105 centered.
        vm.ResizeFrame(CropCorner.BottomRight, -200, 0);

        Assert.Equal(70, vm.PreviewViewWidth, 3);
        Assert.Equal(105, vm.PreviewViewHeight, 3);
        Assert.Equal(35, vm.PreviewViewLeft, 3); // (140-70)/2 — symmetric margins
        Assert.Equal(0, vm.PreviewViewTop, 3);
    }
}

namespace SmartCon.Core.Services.FamilyManager;

/// <summary>
/// Presentation contract for the avatar crop dialog viewport controller.
/// Lets SmartCon.UI crop behaviors drive the dialog without referencing the
/// concrete FamilyManager view-model (no WPF local-type XAML double-compile).
/// Implemented by CropAvatarViewModel.
/// </summary>
public interface ICropAvatarController
{
    void PanBy(double dx, double dy);

    void ZoomAt(double factor, double viewX, double viewY);

    void MoveFrame(double dx, double dy);

    void ResizeFrame(CropCorner corner, double dx, double dy);
}